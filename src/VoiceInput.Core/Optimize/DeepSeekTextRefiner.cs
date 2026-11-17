using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Text;

namespace VoiceInput.Core.Optimize;

/// <summary>
/// OpenAI 兼容对话接口的优化器：组请求、发一次、解析、按本地守卫判定合格，不合格就
/// 抛异常让状态机回退原文。它不重试：用户正等着文字落进窗口，重试只会把等待翻倍。
/// </summary>
public sealed class DeepSeekTextRefiner : ITextRefiner, IDisposable
{
    public const string ChatPath = "/chat/completions";

    /// <summary>
    /// 中文不转成 \uXXXX：请求体本来就是 UTF-8，转义只会让日志里的原文没法读，
    /// 体积也变大。
    /// </summary>
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly RefineOptions _options;

    private bool _disposed;

    /// <summary><paramref name="handler"/> 只为测试注入，生产传 null。</summary>
    public DeepSeekTextRefiner(
        string apiKey,
        string baseUrl,
        RefineOptions options,
        HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _options = options;

        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Description => RefineModes.ToConfigValue(_options.Mode);

    public async Task<string> RefineAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = await SendAsync(text, cancellationToken).ConfigureAwait(false);
        var refined = TextNormalizer.Normalize(ReadContent(body));

        Guard(text, refined);
        return refined;
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

    private async Task<string> SendAsync(string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            using var request = BuildRequest(text);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Failure(
                    DescribeStatus(response.StatusCode),
                    detail: $"HTTP {(int)response.StatusCode}");
            }

            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure($"读取超时 {_options.TimeoutSeconds} 秒", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw Failure("网络请求失败", detail: ex.Message, inner: ex);
        }
    }

    private HttpRequestMessage BuildRequest(string text)
    {
        // 温度 0：校对要的是稳定复现，不是多样表达。
        // 默认显式关掉思考：该端点默认会思考，校对不需要，实测耗时翻倍且容易超时。
        // 个别端点不认这个字段而返回 400 时，按优化失败回退原文；把 aiOptimizeThinking
        // 设成 true 就能退回旧行为。
        // 用字典而不是两个匿名对象：公共字段只写一份，免得改模型/温度时只改到其中一条路径。
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = RefinePrompt.Build(_options.Mode) },
                new { role = "user", content = text },
            },
            ["stream"] = false,
            ["temperature"] = 0,
        };

        if (!_options.Thinking)
        {
            payload["thinking"] = new { type = "disabled" };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + ChatPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, BodyOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    /// <summary>响应只认 <c>choices[0].message.content</c>；其余形状一律当解析失败。</summary>
    private static string ReadContent(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw Failure("响应不是 JSON", detail: TextNormalizer.OneLine(body), inner: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }

        throw Failure("响应里没有 choices[0].message.content", detail: TextNormalizer.OneLine(body));
    }

    /// <summary>四条本地护栏。模型不听话是常态，不能只靠提示词。</summary>
    private void Guard(string original, string refined)
    {
        if (string.IsNullOrWhiteSpace(refined))
        {
            throw Failure("优化结果为空");
        }

        if (refined.Length > original.Length * 2 + 10)
        {
            throw Failure(
                $"优化结果长度异常：{refined.Length} 字，原文 {original.Length} 字");
        }

        if (_options.Mode == RefineMode.Punctuation && !SameCharacters(original, refined))
        {
            throw Failure("仅修复标点模式改动了文字，已回退");
        }

        if (_options.Mode != RefineMode.Punctuation && !SharesEnoughContent(original, refined))
        {
            throw Failure("优化结果与原文相差太大，已回退");
        }
    }

    /// <summary>只比较文字本身：标点与空白全去掉后必须逐字相同。</summary>
    private static bool SameCharacters(string left, string right) =>
        string.Equals(StripPunctuation(left), StripPunctuation(right), StringComparison.Ordinal);

    /// <summary>
    /// 原文的文字至少三分之一出现在结果里。挡的是“抱歉，我无法完成这个请求”这类拒答，
    /// 以及整句被删；润色会调语序、删语气词，但字还在，不会误伤。阈值取三分之一而不是
    /// 一半：一句话里连说几个“嗯、那个”时，去掉语气词后的重合度可能只有四成。
    /// </summary>
    private static bool SharesEnoughContent(string original, string refined)
    {
        var source = StripPunctuation(original);
        if (source.Length == 0)
        {
            return true;
        }

        var remaining = new Dictionary<char, int>();
        foreach (var character in StripPunctuation(refined))
        {
            remaining[character] = remaining.GetValueOrDefault(character) + 1;
        }

        var shared = 0;
        foreach (var character in source)
        {
            if (remaining.TryGetValue(character, out var count) && count > 0)
            {
                remaining[character] = count - 1;
                shared++;
            }
        }

        return shared * 3 >= source.Length;
    }

    /// <summary>
    /// 只去掉标点与空白。符号（¥、$、+、℃）和数字一样是内容，不能当标点滤掉，
    /// 否则“一共¥300”被改成“一共300”也能通过守卫。
    /// </summary>
    private static string StripPunctuation(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsPunctuation(character) || char.IsWhiteSpace(character) || character is '～' or '~')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "API Key 无效或已失效",
        HttpStatusCode.TooManyRequests => "请求过于频繁",
        _ when (int)status >= 500 => "服务暂时不可用",
        _ => "请求被拒绝",
    };

    private static VoiceInputException Failure(string message, string? detail = null, Exception? inner = null) =>
        new(
            ErrorCodes.AiOptimizeFailed,
            ErrorCatalog.AiOptimizeFailed,
            detail: detail is null ? message : message + "：" + detail,
            inner: inner);
}
