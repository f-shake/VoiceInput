using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Tests.Fakes;

namespace VoiceInput.Core.Tests;

public sealed class VoiceInputRunnerTests
{
    private sealed record Context(
        VoiceInputRunner Runner,
        FakeRecorder Recorder,
        FakePaster Paster,
        RecordingStatus Status,
        ToneLog Tones,
        ListLog Log);

    private static Context Build(
        ITranscriber? transcriber = null,
        FakeRecorder? recorder = null,
        FakePaster? paster = null,
        AppConfig? config = null)
    {
        var rec = recorder ?? new FakeRecorder();
        var pa = paster ?? new FakePaster();
        var status = new RecordingStatus();
        var tones = new ToneLog();
        var log = new ListLog();
        var runner = new VoiceInputRunner(
            config ?? new AppConfig { MinSeconds = 0.3 },
            rec,
            transcriber ?? new FakeTranscriber(),
            pa,
            status,
            tones,
            log);
        return new Context(runner, rec, pa, status, tones, log);
    }

    [Fact]
    public async Task 一轮完整流程以粘贴成功收尾()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, transcriber.Calls);
        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
        Assert.Equal(OverlayState.Success, context.Status.LastState);
        Assert.Equal("已粘贴 2 字", context.Status.LastText);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
        Assert.Equal(new[] { ToneKind.Start, ToneKind.Stop }, context.Tones.Played);
    }

    [Fact]
    public async Task 按下即显示录音中并按顺序播放提示音()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(OverlayState.Recording, context.Status.Entries[0].State);
        Assert.Equal("录音中", context.Status.Entries[0].Text);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }

    [Fact]
    public async Task 录音过短不发请求()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { Result = new Recording(new byte[] { 1 }, 0.1, false) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(context.Paster.Writes);
        Assert.Equal(OverlayState.Failure, context.Status.LastState);
        Assert.Equal(ErrorCatalog.RecordingTooShort, context.Status.LastText);
        Assert.True(context.Log.Contains(ErrorCodes.RecordingTooShort));
    }

    [Fact]
    public async Task 时长恰好等于下限时照常提交()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { Result = new Recording(new byte[] { 1 }, 0.3, false) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, transcriber.Calls);
    }

    [Fact]
    public async Task 没有录音数据时按过短处理()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { Result = new Recording(null!, 1.0, false) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Equal(ErrorCatalog.RecordingTooShort, context.Status.LastText);
    }

    [Fact]
    public async Task 转写结果为空时不碰剪贴板()
    {
        var transcriber = new FakeTranscriber { Result = new AsrResult("   ", 0, "req-9", 0.1) };
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Empty(context.Paster.Writes);
        Assert.Equal(ErrorCatalog.NoSpeech, context.Status.LastText);
    }

    [Fact]
    public async Task 转写失败时状态条给一句人话且日志带错误码()
    {
        var transcriber = new FakeTranscriber
        {
            Error = new VoiceInputException(ErrorCodes.ApiKeyRejected, ErrorCatalog.ApiKeyRejected),
        };
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Empty(context.Paster.Writes);
        Assert.Equal(ErrorCatalog.ApiKeyRejected, context.Status.LastText);
        Assert.True(context.Log.Contains(ErrorCodes.ApiKeyRejected));
    }

    [Fact]
    public async Task 粘贴失败时把转写文本本身写进日志()
    {
        var paster = new FakePaster
        {
            Error = new VoiceInputException(ErrorCodes.ClipboardWriteFailed, ErrorCatalog.ClipboardWriteFailed),
        };
        var context = Build(paster: paster);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(ErrorCatalog.ClipboardWriteFailed, context.Status.LastText);
        Assert.True(context.Log.Contains("你好"));
    }

    [Fact]
    public async Task 录音期间按了别的键就整段丢弃()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        context.Runner.NoteOtherKey("C");
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(context.Paster.Writes);
        Assert.Equal(ErrorCatalog.ComboCancelled, context.Status.LastText);
    }

    [Fact]
    public async Task 转写进行中再按热键只记一条日志()
    {
        var transcriber = new BlockingTranscriber();
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        var transcribing = context.Runner.HotkeyUpAsync();
        await context.Recorder.Started.Task;
        Assert.Equal(RunnerState.Transcribing, context.Runner.State);

        context.Runner.HotkeyDown();
        Assert.Equal(1, context.Recorder.Calls);
        Assert.True(context.Log.Contains(ErrorCodes.Busy));

        transcriber.Source.SetResult(new AsrResult("你好", 800, "req-1", 0.2));
        await transcribing;

        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }

    [Fact]
    public async Task 按住不放时重复的按下事件不重启录音()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, context.Recorder.Calls);
        Assert.Single(context.Status.Entries, entry => entry.Text == "录音中");
    }

    [Fact]
    public async Task 超过上限时先提示截断再继续转写()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(
            transcriber,
            recorder: new FakeRecorder { Result = new Recording(new byte[] { 1 }, 118, true) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Contains("118", context.Status.Entries[1].Text, StringComparison.Ordinal);
        Assert.Equal(OverlayState.Success, context.Status.LastState);
    }

    [Fact]
    public async Task 录音器报错时按错误文案收尾()
    {
        var transcriber = new FakeTranscriber();
        var recorder = new FakeRecorder
        {
            Error = new VoiceInputException(ErrorCodes.DeviceUnavailable, ErrorCatalog.DeviceUnavailable),
        };
        var context = Build(transcriber, recorder: recorder);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Equal(ErrorCatalog.ClampForStatus(ErrorCatalog.DeviceUnavailable), context.Status.LastText);
    }

    [Fact]
    public async Task 剪贴板还原失败只告警不影响成功()
    {
        var paster = new FakePaster { Result = new PasteResult(true, false, true) };
        var context = Build(paster: paster);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.True(context.Log.Contains(ErrorCatalog.ClipboardRestoreFailed));
        Assert.Equal(OverlayState.Success, context.Status.LastState);
    }

    [Fact]
    public async Task 未按下就松开不会启动任何流程()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(context.Status.Entries);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }
}
