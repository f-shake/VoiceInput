using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Providers;
using VoiceInput.Core.Text;

namespace VoiceInput.Providers.DeepSeek;

/// <summary>
/// DeepSeek 云端 ASR 的转写器。只做四件事：组 multipart、发请求、按状态码决定重不
/// 重试、把响应变成 <see cref="AsrResult"/>。它不判断“这段文本值不值得粘贴”。
///
/// 所有 DeepSeek 专有的东西（路径、字段名、响应结构、状态码映射）都关在这个
/// 程序集里，换一家厂商时宿主一行都不用改。
/// </summary>
public sealed class DeepSeekTranscriber : ITranscriber, IDisposable
{
    public const string AsrPath = "/v1/tools/asr";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly AsrTuning _tuning;
    private readonly Func<double, CancellationToken, Task> _delay;
    private volatile bool _disposed;

    public DeepSeekTranscriber(
        string apiKey,
        string baseUrl,
        AsrTuning tuning,
        HttpMessageHandler? handler = null,
        Func<double, CancellationToken, Task>? delay = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _tuning = tuning;
        _delay = delay ?? ((seconds, token) => Task.Delay(TimeSpan.FromSeconds(seconds), token));

        // 超时全部由 CancellationToken 控制，HttpClient 自身的 Timeout 留成无限，
        // 否则它会在读取超时之前先抛一个与配置对不上的异常。
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(tuning.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>状态机只认这个入口，文件名由这里固定。</summary>
    public Task<AsrResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken) =>
        TranscribeAsync(wavBytes, "utterance.wav", cancellationToken);

    public async Task<AsrResult> TranscribeAsync(
        byte[] wavBytes,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var url = _baseUrl + AsrPath;
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_tuning.ReadTimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                using var request = BuildRequest(url, wavBytes, fileName);
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var timeoutError = BuildTimeoutError(attempt, ex);
                if (attempt < _tuning.Retries)
                {
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                throw timeoutError;
            }
            catch (HttpRequestException ex)
            {
                var networkError = BuildNetworkError(ex.Message, attempt, ex);
                if (attempt < _tuning.Retries)
                {
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                throw networkError;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return await ParseAsync(response, stopwatch.Elapsed.TotalSeconds, cancellationToken)
                        .ConfigureAwait(false);
                }

                // 先用候选错误问“这类错值不值得重试”，省掉一份重复的状态码判断；
                // 真正重试时这份文案被丢掉，最终抛出的那条才带实际重试次数。
                var error = BuildStatusError(response, attempt);
                if (error.Retryable && attempt < _tuning.Retries)
                {
                    await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                throw error;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    private double BackoffFor(int attempt)
    {
        var backoff = _tuning.RetryBackoffSeconds;
        if (backoff.Count == 0)
        {
            return 0;
        }

        return attempt < backoff.Count ? backoff[attempt] : backoff[^1];
    }

    private HttpRequestMessage BuildRequest(string url, byte[] wavBytes, string fileName)
    {
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var content = new MultipartFormDataContent();
        content.Add(file, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private VoiceInputException BuildTimeoutError(int attempts, Exception inner) =>
        new(
            ErrorCodes.NetworkFailed,
            ErrorCatalog.NetworkFailed(attempts),
            retryable: true,
            detail: $"读取超时 {_tuning.ReadTimeoutSeconds} 秒",
            inner: inner);

    private static VoiceInputException BuildNetworkError(string detail, int attempts, Exception inner) =>
        new(ErrorCodes.NetworkFailed, ErrorCatalog.NetworkFailed(attempts), retryable: true, detail: detail, inner: inner);

    private static VoiceInputException BuildStatusError(HttpResponseMessage response, int attempts) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new VoiceInputException(
                ErrorCodes.ApiKeyRejected, ErrorCatalog.ApiKeyRejected),
            HttpStatusCode.TooManyRequests => new VoiceInputException(
                ErrorCodes.RateLimited, ErrorCatalog.RateLimited, retryable: true),
            _ when (int)response.StatusCode >= 500 => new VoiceInputException(
                ErrorCodes.ServiceUnavailable,
                ErrorCatalog.ServiceUnavailable(attempts),
                retryable: true,
                detail: $"HTTP {(int)response.StatusCode}"),
            _ => new VoiceInputException(
                ErrorCodes.AudioRejected,
                ErrorCatalog.AudioRejected,
                detail: $"HTTP {(int)response.StatusCode}"),
        };

    private static async Task<AsrResult> ParseAsync(
        HttpResponseMessage response,
        double elapsedSeconds,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new VoiceInputException(
                ErrorCodes.UnparsableResponse,
                ErrorCatalog.NoSpeech,
                detail: TextNormalizer.OneLine(body),
                inner: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                throw new VoiceInputException(
                    ErrorCodes.UnparsableResponse,
                    ErrorCatalog.NoSpeech,
                    detail: TextNormalizer.OneLine(body));
            }

            return new AsrResult(
                TextNormalizer.Normalize(textElement.GetString()),
                ReadInt(root, "duration_ms"),
                ReadString(root, "request_id"),
                elapsedSeconds);
        }
    }

    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return element.TryGetInt32(out var value) ? value : 0;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return element.GetString() ?? string.Empty;
    }
}
