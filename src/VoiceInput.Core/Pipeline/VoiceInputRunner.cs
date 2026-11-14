using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;

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
    private readonly AppConfig _config;
    private readonly IRecorder _recorder;
    private readonly IPaster _paster;
    private readonly IStatusView _status;
    private readonly IFeedback _feedback;
    private readonly IDiagnosticsLog _log;
    private readonly object _gate = new();

    /// <summary>设置里换了引擎或 Key 时会替换，所以不是 readonly；引用赋值本身是原子的。</summary>
    private volatile ITranscriber _transcriber;

    private RunnerState _state = RunnerState.Idle;
    private bool _guardTripped;
    private string _guardKey = string.Empty;
    private CancellationTokenSource? _stopRecording;
    private Task<Recording>? _recording;

    public VoiceInputRunner(
        AppConfig config,
        IRecorder recorder,
        ITranscriber transcriber,
        IPaster paster,
        IStatusView status,
        IFeedback feedback,
        IDiagnosticsLog log)
    {
        _config = config;
        _recorder = recorder;
        _transcriber = transcriber;
        _paster = paster;
        _status = status;
        _feedback = feedback;
        _log = log;
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

    public void HotkeyDown()
    {
        var busy = false;
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
            else
            {
                _state = RunnerState.Recording;
                _guardTripped = false;
                _guardKey = string.Empty;
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

        _feedback.Play(ToneKind.Start);
        _status.Show(OverlayState.Recording, ErrorCatalog.Recording);
    }

    /// <summary>录音期间按下其它键，说明用户在打组合键，本次听写作废。</summary>
    public void NoteOtherKey(string keyName)
    {
        lock (_gate)
        {
            if (_state == RunnerState.Recording)
            {
                _guardTripped = true;
                _guardKey = keyName;
            }
        }
    }

    public async Task HotkeyUpAsync()
    {
        CancellationTokenSource? stop;
        Task<Recording>? recordingTask;
        bool guard;

        lock (_gate)
        {
            if (_state != RunnerState.Recording)
            {
                return;
            }

            _state = RunnerState.Transcribing;
            guard = _guardTripped;
            stop = _stopRecording;
            recordingTask = _recording;
            _stopRecording = null;
            _recording = null;
        }

        _feedback.Play(ToneKind.Stop);
        stop?.Cancel();

        try
        {
            await RunPipelineAsync(recordingTask, guard).ConfigureAwait(false);
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

    private async Task RunPipelineAsync(Task<Recording>? recordingTask, bool guard)
    {
        if (guard)
        {
            _log.Info($"{ErrorCodes.ComboCancelled} 录音期间按下了 {_guardKey}，整段丢弃。");
            Fail(ErrorCatalog.ComboCancelled);
            return;
        }

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

        try
        {
            var paste = _paster.Paste(result.Text);
            if (!paste.Restored && paste.HadBackup)
            {
                _log.Warn(ErrorCatalog.ClipboardRestoreFailed);
            }
        }
        catch (VoiceInputException ex)
        {
            // E10 的文案承诺“结果已写入日志”，所以这里必须把文本本身落盘。
            _log.Error(result.Text);
            FailWith(ex);
            return;
        }

        _log.Info(
            $"已粘贴 {result.Text.Length} 字，服务端 {result.DurationMs} 毫秒，" +
            $"耗时 {result.ElapsedSeconds:F2} 秒，request_id={result.RequestId}。");
        _status.Show(OverlayState.Success, ErrorCatalog.ClampForStatus(ErrorCatalog.Pasted(result.Text.Length)));
    }

    private void Fail(string message) =>
        _status.Show(OverlayState.Failure, ErrorCatalog.ClampForStatus(message));

    private void FailWith(VoiceInputException ex)
    {
        var detail = string.IsNullOrEmpty(ex.Detail) ? string.Empty : " " + ex.Detail;
        _log.Error($"{ex.Code} {ex.UserMessage}{detail}");
        Fail(ex.UserMessage);
    }
}
