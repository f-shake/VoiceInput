using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceInput.Core.Optimize;

/// <summary>
/// 从接口地址拉取可用模型列表（OpenAI 兼容的 GET /models），只给设置窗口的下拉用。
///
/// 拉取失败一律抛异常，由调用方决定怎么办：设置窗口会保持现有候选并弹错误框。
/// 这里不做重试——用户正盯着下拉框。
/// </summary>
public sealed class ModelCatalog : IDisposable
{
    public const string ModelsPath = "/models";

    /// <summary>预取超时。设得比转写短得多：多等几秒不如直接让人手输模型名。</summary>
    public const double DefaultTimeoutSeconds = 5;

    /// <summary>再长的列表对下拉框没有意义，反而拖慢展开。</summary>
    private const int MaxModels = 200;

    private readonly HttpClient _http;

    /// <summary><paramref name="handler"/> 只为测试注入，生产传 null。</summary>
    public ModelCatalog(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // 超时全部交给 CancellationToken：HttpClient 自己的超时会先于配置触发，
        // 抛出来的异常与设置对不上。
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<string>> ListAsync(
        string baseUrl,
        string apiKey,
        double timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var url = baseUrl.TrimEnd('/') + ModelsPath;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Parse(body);
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

    private bool _disposed;

    /// <summary>
    /// 兼容两种形状：OpenAI 的 <c>{"data":[{"id":"…"}]}</c>，以及直接返回字符串数组的
    /// 简化端点。认不出的形状按“没有模型”处理，由调用方决定接着怎么提示。
    /// </summary>
    private static IReadOnlyList<string> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var models = new List<string>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            Collect(data, models);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            Collect(root, models);
        }

        return models;
    }

    private static void Collect(JsonElement array, List<string> models)
    {
        foreach (var item in array.EnumerateArray())
        {
            var id = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out var idElement)
                                          && idElement.ValueKind == JsonValueKind.String
                    => idElement.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(id) && !models.Contains(id, StringComparer.Ordinal))
            {
                models.Add(id);
            }

            if (models.Count >= MaxModels)
            {
                return;
            }
        }
    }
}
