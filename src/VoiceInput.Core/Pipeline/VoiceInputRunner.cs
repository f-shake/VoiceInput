using System.Diagnostics;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Pipeline;

public enum RunnerState
{
    Idle,
    Recording,
    Transcribing,
}

public enum OverlayState
{
    Hidden,
    Recording,
    Transcribing,
    Success,

    /// <summary>粘贴成功但结果优化失败：橙色，与红色的整体失败区分开。</summary>
    Warning,
    Failure,
}

public enum ToneKind
{
    Start,
    Stop,
    Error,
}

public readonly record struct Recording(byte[] WavBytes, double DurationSeconds, bool Truncated);

public readonly record struct PasteResult(bool Pasted, bool Restored, bool HadBackup);

public interface IRecorder
{
    /// <summary>录到 <paramref name="stopToken"/> 被取消或到达上限为止。</summary>
    Recording Record(CancellationToken stopToken);
}

public interface ITranscriber
{
    Task<AsrResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken);
}

public interface IPaster
{
    PasteResult Paste(string text);
}

public interface IStatusView
{
    void Show(OverlayState state, string text);
}

public interface IFeedback
{
    void Play(ToneKind kind);
}

/// <summary>
/// 状态机：把热键事件变成“录音→转写→粘贴”，全部外部依赖都是注入的接口，
/// 所以这段逻辑能在没有麦克风、没有网络、没有窗口的环境里跑完。
/// </summary>
public sealed class VoiceInputRunner
{
    /// <summary>优化失败时写进日志的那半句。两处 catch 共用，免得改一处漏一处。</summary>
    private const string FallbackNote = "结果优化失败，已使用原始文本";

    private readonly AppConfig _config;
    private readonly IRecorder _recorder;
    private readonly IPaster _paster;
    private readonly IStatusView _status;
    private readonly IFeedback _feedback;
    private readonly IDiagnosticsLog _log;
    private readonly Func<bool>? _keepTrailingPunctuation;
    private readonly object _gate = new();

    /// <summary>设置里换了引擎或 Key 时会替换，所以不是 readonly；引用赋值本身是原子的。</summary>
    private volatile ITranscriber _transcriber;

    /// <summary>选“不优化”时为 null。设置变更时由装配层替换，所以也不是 readonly。</summary>
    private volatile ITextRefiner? _refiner;

    private RunnerState _state = RunnerState.Idle;

    /// <summary>
    /// 本次按住热键期间已经被判定为组合键。必须一直挡到松开热键为止：否则长按产生的
    /// 自动重复，或者“别的键先松开、热键还按着”，都会被当成一次新的干净按下。
    /// </summary>
    private bool _chordIgnored;

    private CancellationTokenSource? _stopRecording;
    private Task<Recording>? _recording;

    public VoiceInputRunner(
        AppConfig config,
        IRecorder recorder,
        ITranscriber transcriber,
        IPaster paster,
        IStatusView status,
        IFeedback feedback,
        IDiagnosticsLog log,
        ITextRefiner? refiner = null,
        Func<bool>? keepTrailingPunctuation = null)
    {
        _config = config;
        _recorder = recorder;
        _transcriber = transcriber;
        _refiner = refiner;
        _paster = paster;
        _status = status;
        _feedback = feedback;
        _log = log;
        // 用委托而不是快照：设置里改了“保留句末标点”，下一次听写就该按新值走。
        _keepTrailingPunctuation = keepTrailingPunctuation;
    }

    public RunnerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>换引擎或换 Key 后由装配层调用；下一次录音就用新的转写器。</summary>
    public void ReplaceTranscriber(ITranscriber transcriber) =>
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));

    /// <summary>设置里改了优化方式、Key、模型或地址后替换；传 null 表示“不优化”。</summary>
    public void ReplaceRefiner(ITextRefiner? refiner) => _refiner = refiner;

    /// <summary>不知道“同时还按着什么键”时的入口，等价于一次干净按下。</summary>
    public void HotkeyDown() => HotkeyDown(Array.Empty<string>());

    /// <summary>
    /// 按下热键。<paramref name="otherKeysHeld"/> 是按下这一刻还按着的其它键：非空说明用户
    /// 要打的是别人的组合键（右 Ctrl 本身就是修饰键），本次不启动听写。
    /// </summary>
    public void HotkeyDown(IReadOnlyCollection<string> otherKeysHeld)
    {
        ArgumentNullException.ThrowIfNull(otherKeysHeld);

        var busy = false;
        var chorded = false;
        string? chordNote = null;

        lock (_gate)
        {
            if (_state == RunnerState.Recording)
            {
                // 按住不放时系统会持续补发按下事件，这里必须静默忽略。
                return;
            }

            if (_state == RunnerState.Transcribing)
            {
                busy = true;
            }
            else if (otherKeysHeld.Count > 0 || _chordIgnored)
            {
                chorded = true;

                // 一次按住只记一条日志：自动重复会连着来十几条。
                chordNote = _chordIgnored ? null : string.Join("、", otherKeysHeld);
                _chordIgnored = true;
            }
            else
            {
                _state = RunnerState.Recording;
                _stopRecording = new CancellationTokenSource();
                var token = _stopRecording.Token;
                _recording = Task.Run(() => _recorder.Record(token));
            }
        }

        if (busy)
        {
            _log.Info($"{ErrorCodes.Busy} 转写进行中又按下了热键。");
            return;
        }

        if (chorded)
        {
            if (chordNote is not null)
            {
                _log.Info($"{ErrorCodes.ComboCancelled} 按下热键时 {chordNote} 还按着，不启动。");
            }

            return;
        }

        _feedback.Play(ToneKind.Start);
        _status.Show(OverlayState.Recording, ErrorCatalog.Recording);
    }

    /// <summary>
    /// 录音期间按下了别的键：说明用户打的是组合键，本次听写作废。
    /// 必须当场取消——等到热键松开才提示，那段时间的录音既没人要，用户还得一直看着“录音中”。
    /// </summary>
    public void NoteOtherKey(string keyName)
    {
        CancellationTokenSource? stop;
        Task<Recording>? recordingTask;

        lock (_gate)
        {
            if (_state != RunnerState.Recording)
            {
                return;
            }

            _state = RunnerState.Idle;
            _chordIgnored = true;
            stop = _stopRecording;
            recordingTask = _recording;
            _stopRecording = null;
            _recording = null;
        }

        _log.Info($"{ErrorCodes.ComboCancelled} 录音期间按下了 {keyName}，立即取消。");
        _feedback.Play(ToneKind.Error);
        Fail(ErrorCatalog.ComboCancelled);
        stop?.Cancel();

        // 钩子线程上不能等录音停下：交给后台收尾，这里只负责把取消源放掉。
        _ = DiscardRecordingAsync(recordingTask, stop);
    }

    public async Task HotkeyUpAsync()
    {
        CancellationTokenSource? stop;
        Task<Recording>? recordingTask;

        lock (_gate)
        {
            // 一次按住到此结束，下一次按下重新判断是不是组合键。
            _chordIgnored = false;

            if (_state != RunnerState.Recording)
            {
                return;
            }

            _state = RunnerState.Transcribing;
            stop = _stopRecording;
            recordingTask = _recording;
            _stopRecording = null;
            _recording = null;
        }

        _feedback.Play(ToneKind.Stop);
        stop?.Cancel();

        try
        {
            await RunPipelineAsync(recordingTask).ConfigureAwait(false);
        }
        catch (VoiceInputException ex)
        {
            FailWith(ex);
        }
        catch (Exception ex)
        {
            FailWith(new VoiceInputException(
                ErrorCodes.Unexpected, ErrorCatalog.Unexpected, detail: ex.Message, inner: ex));
        }
        finally
        {
            stop?.Dispose();
            lock (_gate)
            {
                _state = RunnerState.Idle;
            }
        }
    }

    private async Task RunPipelineAsync(Task<Recording>? recordingTask)
    {
        Recording recording;
        try
        {
            recording = recordingTask is null ? default : await recordingTask.ConfigureAwait(false);
        }
        catch (VoiceInputException ex)
        {
            FailWith(ex);
            return;
        }

        if (recording.WavBytes is null || recording.DurationSeconds < _config.MinSeconds)
        {
            _log.Info($"{ErrorCodes.RecordingTooShort} 时长 {recording.DurationSeconds:F2} 秒。");
            Fail(ErrorCatalog.RecordingTooShort);
            return;
        }

        if (recording.Truncated)
        {
            var truncated = ErrorCatalog.Truncated(_config.MaxSeconds);
            _log.Warn(truncated);
            _status.Show(OverlayState.Transcribing, ErrorCatalog.ClampForStatus(truncated));
        }
        else
        {
            _status.Show(OverlayState.Transcribing, ErrorCatalog.Transcribing);
        }

        var result = await _transcriber.TranscribeAsync(recording.WavBytes, CancellationToken.None)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            // 记上音频时长：能区分“说完就松手、没录到语音”和“麦克风根本没拾音”。
            _log.Info(
                $"{ErrorCodes.UnparsableResponse} 服务端返回空文本，音频 {recording.DurationSeconds:F1} 秒，" +
                $"request_id={result.RequestId}。");
            Fail(ErrorCatalog.NoSpeech);
            return;
        }

        var text = result.Text;
        var optimizeNote = "结果优化 不优化";
        var optimizeApplied = false;
        var optimizeFailed = false;
        if (_refiner is { } refiner)
        {
            _status.Show(OverlayState.Transcribing, ErrorCatalog.Optimizing);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                text = await refiner.RefineAsync(text, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 契约上优化器不会返回空串；真发生了也当失败，别把空文本粘出去。
                    throw new VoiceInputException(
                        ErrorCodes.AiOptimizeFailed, ErrorCatalog.AiOptimizeFailed, detail: "优化结果为空");
                }

                optimizeApplied = true;
                optimizeNote =
                    $"结果优化 {refiner.Description}，优化耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} 毫秒";
            }
            catch (VoiceInputException ex)
            {
                _log.Warn($"{ex.Code} {ErrorCatalog.AiOptimizeFailed} 原因：{ex.Detail ?? ex.UserMessage}");
                text = result.Text;
                optimizeNote = FallbackNote;
                optimizeFailed = true;
            }
            catch (Exception ex)
            {
                _log.Warn($"{ErrorCodes.AiOptimizeFailed} {ErrorCatalog.AiOptimizeFailed} 原因：{ex.Message}");
                text = result.Text;
                optimizeNote = FallbackNote;
                optimizeFailed = true;
            }
        }

        // 句末标点与“优化方式”无关：选“不优化”时对原文照样生效。
        text = TrailingPunctuation.Apply(text, _keepTrailingPunctuation?.Invoke() ?? true);
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Info($"{ErrorCodes.UnparsableResponse} 去掉句末标点后没有可粘贴的文字。");
            Fail(ErrorCatalog.NoSpeech);
            return;
        }

        try
        {
            var paste = _paster.Paste(text);
            if (!paste.Restored && paste.HadBackup)
            {
                _log.Warn(ErrorCatalog.ClipboardRestoreFailed);
            }
        }
        catch (VoiceInputException ex)
        {
            // E10 的文案承诺“结果已写入日志”，所以这里必须把文本本身落盘。
            _log.Error(text);
            FailWith(ex);
            return;
        }

        _log.Info(
            $"已粘贴 {text.Length} 字，服务端 {result.DurationMs} 毫秒，" +
            $"耗时 {result.ElapsedSeconds:F2} 秒，request_id={result.RequestId}，{optimizeNote}。");

        // 优化失败但粘贴成功时用橙色：红叉是“这次没成功”，橙圆是“文字进去了，
        // 但优化没生效”，两者的处置完全不同。
        var state = optimizeFailed ? OverlayState.Warning : OverlayState.Success;
        var message = optimizeFailed
            ? ErrorCatalog.OptimizeFailedPasted(text.Length)
            : optimizeApplied
                ? ErrorCatalog.Optimized(text.Length)
                : ErrorCatalog.Pasted(text.Length);
        _status.Show(state, ErrorCatalog.ClampForStatus(message));
    }

    private void Fail(string message) =>
        _status.Show(OverlayState.Failure, ErrorCatalog.ClampForStatus(message));

    private void FailWith(VoiceInputException ex)
    {
        var detail = string.IsNullOrEmpty(ex.Detail) ? string.Empty : " " + ex.Detail;
        _log.Error($"{ex.Code} {ex.UserMessage}{detail}");
        Fail(ex.UserMessage);
    }

    /// <summary>
    /// 被组合键作废的录音已经在取消，但它的任务再没有人 await 了，得有人收尾：
    /// 等它真的停下来（取消异常本来就没有接收方，吞掉即可）再把取消源释放掉。
    /// </summary>
    private async Task DiscardRecordingAsync(Task<Recording>? recording, CancellationTokenSource? stop)
    {
        try
        {
            if (recording is not null)
            {
                await recording.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 录音器可能有自己的失败原因；用户已经看到红叉，这里只留线索，不再打扰。
            _log.Warn($"{ErrorCodes.ComboCancelled} 取消的录音收尾异常：{ex.Message}");
        }
        finally
        {
            stop?.Dispose();
        }
    }
}
