using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;
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
        AppConfig? config = null,
        ITextRefiner? refiner = null,
        Func<bool>? keepTrailingPunctuation = null)
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
            log,
            refiner,
            keepTrailingPunctuation);
        return new Context(runner, rec, pa, status, tones, log);
    }

    [Fact]
    public async Task FullPipelineEndsWithSuccessfulPaste()
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
    public async Task HotkeyDownShowsRecordingStatus()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(OverlayState.Recording, context.Status.Entries[0].State);
        Assert.Equal("录音中", context.Status.Entries[0].Text);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }

    [Fact]
    public async Task ShortRecordingSkipsTranscription()
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
    public async Task ExactMinimumDurationIsStillSubmitted()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { Result = new Recording(new byte[] { 1 }, 0.3, false) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, transcriber.Calls);
    }

    [Fact]
    public async Task MissingRecordingDataIsTreatedAsTooShort()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { Result = new Recording(null!, 1.0, false) });

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Equal(ErrorCatalog.RecordingTooShort, context.Status.LastText);
    }

    [Fact]
    public async Task EmptyTranscriptionSkipsClipboard()
    {
        var transcriber = new FakeTranscriber { Result = new AsrResult("   ", 0, "req-9", 0.1) };
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Empty(context.Paster.Writes);
        Assert.Equal(ErrorCatalog.NoSpeech, context.Status.LastText);
    }

    [Fact]
    public async Task TranscriptionFailureShowsMessageAndErrorCode()
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
    public async Task PasteFailureLogsTranscribedText()
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
    public async Task OtherKeyPressStopsRecordingImmediately()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber, recorder: new FakeRecorder { WaitForCancel = true });

        context.Runner.HotkeyDown();
        await context.Recorder.Started.Task;

        context.Runner.NoteOtherKey("C");

        // 提示发生在按下 C 的那一刻，此时热键还没松开。
        Assert.Equal(OverlayState.Failure, context.Status.LastState);
        Assert.Equal(ErrorCatalog.ComboCancelled, context.Status.LastText);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
        Assert.Equal(new[] { ToneKind.Start, ToneKind.Error }, context.Tones.Played);

        // 取消真的送到了录音器，而不是攥到松手才丢。
        await context.Recorder.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 松开热键：不再提示、不转写、不碰剪贴板。
        await context.Runner.HotkeyUpAsync();

        Assert.Single(context.Status.Entries, entry => entry.State == OverlayState.Failure);
        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(context.Paster.Writes);
    }

    [Fact]
    public async Task HotkeyPressWithOtherKeyHeldDoesNotStart()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        context.Runner.HotkeyDown(new[] { "LeftCtrl" });

        Assert.Equal(0, context.Recorder.Calls);
        Assert.Empty(context.Status.Entries);
        Assert.Empty(context.Tones.Played);
        Assert.True(context.Log.Contains(ErrorCodes.ComboCancelled));

        await context.Runner.HotkeyUpAsync();

        Assert.Equal(RunnerState.Idle, context.Runner.State);
        Assert.Equal(0, transcriber.Calls);
    }

    [Fact]
    public async Task CleanPressAfterIgnoredPressRecordsNormally()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        context.Runner.HotkeyDown(new[] { "Shift" });
        await context.Runner.HotkeyUpAsync();

        context.Runner.HotkeyDown(Array.Empty<string>());
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, context.Recorder.Calls);
        Assert.Equal(1, transcriber.Calls);
        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
    }

    [Fact]
    public async Task AutoRepeatAfterCancelDoesNotRestart()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        await context.Recorder.Started.Task;

        context.Runner.NoteOtherKey("C");

        // 长按热键时系统会持续补发按下事件；即使别的键已经松开，也不许重新启动。
        context.Runner.HotkeyDown();
        context.Runner.HotkeyDown();

        Assert.Equal(1, context.Recorder.Calls);
        Assert.Single(context.Status.Entries, entry => entry.State == OverlayState.Failure);
        Assert.Single(context.Log.Lines, line => line.Contains(ErrorCodes.ComboCancelled, StringComparison.Ordinal));

        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, context.Recorder.Calls);
    }

    [Fact]
    public async Task HotkeyDuringTranscriptionLogsBusyOnce()
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
    public async Task HeldHotkeyRepeatDoesNotRestartRecording()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, context.Recorder.Calls);
        Assert.Single(context.Status.Entries, entry => entry.Text == "录音中");
    }

    [Fact]
    public async Task OverLimitWarnsTruncationThenTranscribes()
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
    public async Task RecorderErrorEndsWithErrorMessage()
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
    public async Task ClipboardRestoreFailureOnlyWarns()
    {
        var paster = new FakePaster { Result = new PasteResult(true, false, true) };
        var context = Build(paster: paster);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.True(context.Log.Contains(ErrorCatalog.ClipboardRestoreFailed));
        Assert.Equal(OverlayState.Success, context.Status.LastState);
    }

    [Fact]
    public async Task HotkeyUpWithoutDownStartsNothing()
    {
        var transcriber = new FakeTranscriber();
        var context = Build(transcriber);

        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(context.Status.Entries);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }

    [Fact]
    public async Task NoRefinePastesOriginalAndLogsMode()
    {
        var context = Build();

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
        Assert.True(context.Log.Contains("结果优化 不优化"));
        Assert.Equal(OverlayState.Success, context.Status.LastState);
        Assert.Equal("已粘贴 2 字", context.Status.LastText);
    }

    [Fact]
    public async Task RefineSuccessPastesRefinedTextAndMarksOptimized()
    {
        var refiner = new FakeRefiner { Result = "你好，世界。", Description = "fix" };
        var context = Build(refiner: refiner);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(1, refiner.Calls);
        Assert.Equal(new[] { "你好，世界。" }, context.Paster.Writes);
        Assert.Contains(context.Status.Entries, entry => entry.Text == ErrorCatalog.Optimizing);
        Assert.True(context.Log.Contains("结果优化 fix"));
        Assert.Equal(OverlayState.Success, context.Status.LastState);
        Assert.Equal("已粘贴 6 字（已优化）", context.Status.LastText);
    }

    [Fact]
    public async Task RefineFailureFallsBackAndWarns()
    {
        var refiner = new FakeRefiner
        {
            Error = new VoiceInputException(
                ErrorCodes.AiOptimizeFailed, ErrorCatalog.AiOptimizeFailed, detail: "HTTP 500"),
        };
        var context = Build(refiner: refiner);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
        Assert.True(context.Log.Contains(ErrorCodes.AiOptimizeFailed));
        Assert.Equal(OverlayState.Warning, context.Status.LastState);
        Assert.Equal("已粘贴 2 字（优化失败）", context.Status.LastText);
    }

    [Fact]
    public async Task MissingModelShowsRefineFailureWarning()
    {
        var refiner = new UnavailableRefiner("polish", "没有选择模型");
        var context = Build(refiner: refiner);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
        Assert.Equal(OverlayState.Warning, context.Status.LastState);
        Assert.Equal("已粘贴 2 字（优化失败）", context.Status.LastText);
        Assert.True(context.Log.Contains("没有选择模型"));
        Assert.True(context.Log.Contains(ErrorCodes.AiOptimizeFailed));
    }

    [Fact]
    public async Task EmptyRefineResultFallsBackToOriginal()
    {
        var refiner = new FakeRefiner { Result = "   " };
        var context = Build(refiner: refiner);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
        Assert.True(context.Log.Contains("优化结果为空"));
    }

    [Fact]
    public async Task EmptyTranscriptionSkipsRefiner()
    {
        var refiner = new FakeRefiner();
        var transcriber = new FakeTranscriber { Result = new AsrResult("  ", 0, "req-empty", 0.1) };
        var context = Build(transcriber, refiner: refiner);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(0, refiner.Calls);
        Assert.Empty(context.Paster.Writes);
        Assert.Equal(ErrorCatalog.NoSpeech, context.Status.LastText);
    }

    [Fact]
    public async Task TrailingPunctuationOffStripsRawPunctuation()
    {
        var transcriber = new FakeTranscriber { Result = new AsrResult("你好。", 0, "req", 0.1) };
        var context = Build(transcriber, keepTrailingPunctuation: () => false);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好" }, context.Paster.Writes);
    }

    [Fact]
    public async Task TrailingPunctuationKeptByDefault()
    {
        var transcriber = new FakeTranscriber { Result = new AsrResult("你好。", 0, "req", 0.1) };
        var context = Build(transcriber);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好。" }, context.Paster.Writes);
    }

    [Fact]
    public async Task TrailingPunctuationOffStripsRefinedPunctuation()
    {
        var refiner = new FakeRefiner { Result = "你好，世界。" };
        var context = Build(refiner: refiner, keepTrailingPunctuation: () => false);

        context.Runner.HotkeyDown();
        await context.Runner.HotkeyUpAsync();

        Assert.Equal(new[] { "你好，世界" }, context.Paster.Writes);
    }

    [Fact]
    public async Task RefiningShowsOptimizingThenPastes()
    {
        var refiner = new FakeRefiner
        {
            Gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var context = Build(refiner: refiner);

        context.Runner.HotkeyDown();
        var running = context.Runner.HotkeyUpAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!context.Status.Entries.Any(entry => entry.Text == ErrorCatalog.Optimizing)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(context.Status.Entries, entry => entry.Text == ErrorCatalog.Optimizing);
        Assert.Equal(RunnerState.Transcribing, context.Runner.State);

        refiner.Gate.SetResult("优化后的文字");
        await running;

        Assert.Equal(new[] { "优化后的文字" }, context.Paster.Writes);
        Assert.Equal(RunnerState.Idle, context.Runner.State);
    }
}
