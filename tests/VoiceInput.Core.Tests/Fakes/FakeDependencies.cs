using System.Net;
using System.Text;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.Core.Tests.Fakes;

/// <summary>测试替身：把日志、状态、提示音记在内存里，好让断言直接看得到。</summary>
public sealed class ListLog : IDiagnosticsLog
{
    public List<string> Lines { get; } = new();

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Warn(string message) => Lines.Add("WARN " + message);

    public void Error(string message) => Lines.Add("ERROR " + message);

    public bool Contains(string fragment) => Lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
}

public sealed class RecordingStatus : IStatusView
{
    public List<(OverlayState State, string Text)> Entries { get; } = new();

    public OverlayState LastState => Entries.Count == 0 ? OverlayState.Hidden : Entries[^1].State;

    public string LastText => Entries.Count == 0 ? string.Empty : Entries[^1].Text;

    public void Show(OverlayState state, string text) => Entries.Add((state, text));
}

public sealed class ToneLog : IFeedback
{
    public List<ToneKind> Played { get; } = new();

    public void Play(ToneKind kind) => Played.Add(kind);
}

public sealed class FakeRecorder : IRecorder
{
    public Recording Result { get; set; } = new(new byte[] { 1, 2, 3 }, 1.0, false);

    public Exception? Error { get; set; }

    /// <summary>置为 true 时一直阻塞到取消为止，模拟“还在录”的真实录音器。</summary>
    public bool WaitForCancel { get; set; }

    public int Calls { get; private set; }

    public bool SawCancel { get; private set; }

    /// <summary>录音真的开始了才置位。录音跑在线程池上，测试靠它避免抢跑。</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>取消真的送到录音器才置位：用来断言“按下第二个键就停录”，而不是等到松手。</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Recording Record(CancellationToken stopToken)
    {
        Calls++;
        Started.TrySetResult();
        SawCancel = stopToken.IsCancellationRequested;
        if (Error is not null)
        {
            throw Error;
        }

        if (WaitForCancel)
        {
            stopToken.WaitHandle.WaitOne();
            Cancelled.TrySetResult();
            stopToken.ThrowIfCancellationRequested();
        }

        return Result;
    }
}

public sealed class FakeTranscriber : ITranscriber
{
    public AsrResult Result { get; set; } = new("你好", 800, "req-1", 0.2);

    public Exception? Error { get; set; }

    public int Calls { get; private set; }

    public Task<AsrResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        Calls++;
        return Error is null
            ? Task.FromResult(Result)
            : Task.FromException<AsrResult>(Error);
    }
}

/// <summary>可以卡住的转写器，用来在“转写中”这个状态停下来做断言。</summary>
public sealed class BlockingTranscriber : ITranscriber
{
    public TaskCompletionSource<AsrResult> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AsrResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken) => Source.Task;
}

/// <summary>结果优化器的替身：可以正常返回、抛错，或卡住让人观察“优化中”。</summary>
public sealed class FakeRefiner : ITextRefiner
{
    public string Description { get; set; } = "fix";

    public string Result { get; set; } = "优化后的文字";

    public Exception? Error { get; set; }

    public int Calls { get; private set; }

    /// <summary>不为空时用它当返回值，测试可借此把流程停在“优化中”。</summary>
    public TaskCompletionSource<string>? Gate { get; set; }

    public Task<string> RefineAsync(string text, CancellationToken cancellationToken)
    {
        Calls++;
        if (Gate is not null)
        {
            return Gate.Task;
        }

        return Error is null
            ? Task.FromResult(Result)
            : Task.FromException<string>(Error);
    }
}

public sealed class FakePaster : IPaster
{
    public PasteResult Result { get; set; } = new(true, true, false);

    public Exception? Error { get; set; }

    public List<string> Writes { get; } = new();

    public PasteResult Paste(string text)
    {
        if (Error is not null)
        {
            throw Error;
        }

        Writes.Add(text);
        return Result;
    }
}

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public int Calls { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    public FakeHttpHandler Respond(HttpStatusCode status, string body = "")
    {
        _responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public FakeHttpHandler Throw(Exception error)
    {
        _responses.Enqueue(() => throw error);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        // 请求体在 AsrClient 里是 using 的，响应返回后就被释放了，
        // 所以只能趁现在把内容读出来，测试才断言得到 multipart 的字段。
        LastBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        if (_responses.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        return Task.FromResult(_responses.Dequeue()());
    }
}

/// <summary>永远不返回的处理器，用来触发读取超时。</summary>
public sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("不可达");
    }
}
