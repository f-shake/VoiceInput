using VoiceInput.Core.Pipeline;

namespace VoiceInput.Core.Providers;

/// <summary>
/// 识别引擎的插件契约。每个后端编成一个 DLL，实现这个接口，扔进 providers 目录
/// 就能被宿主发现；主程序不认任何具体厂商的路径或字段。
/// </summary>
public interface IAsrProvider
{
    /// <summary>稳定标识，写进配置文件的 provider 字段，不要改。</summary>
    string Id { get; }

    /// <summary>给设置窗口看的名字，可以改。</summary>
    string DisplayName { get; }

    /// <summary>是否必须有 API Key。本地引擎是 false，端口径一致才能统一校验。</summary>
    bool NeedsApiKey { get; }

    /// <summary>是否依赖网络。false 表示断网也能用，界面上可以据此提示。</summary>
    bool NeedsNetwork { get; }

    /// <summary>按当前配置造一个可用的转写器。</summary>
    ITranscriber Create(ProviderContext context);
}

/// <summary>重试与超时。宿主统一解析，插件只负责用。</summary>
public sealed record AsrTuning(
    int Retries,
    IReadOnlyList<double> RetryBackoffSeconds,
    double ConnectTimeoutSeconds,
    double ReadTimeoutSeconds);

/// <summary>
/// 造转写器时给插件的上下文。Key、调优参数与选项都由宿主解析好后传进来；
/// 插件不碰配置文件，也不读环境变量——那两件事只应该有一个实现。
/// </summary>
public sealed record ProviderContext(
    string ApiKey,
    AsrTuning Tuning,
    IReadOnlyDictionary<string, string> Options)
{
    public string Get(string name, string fallback) =>
        Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}

/// <summary>插件被发现后给界面用的描述，不含实例。</summary>
public sealed record ProviderDescriptor(string Id, string DisplayName, bool NeedsApiKey, bool NeedsNetwork)
{
    public static ProviderDescriptor From(IAsrProvider provider) =>
        new(provider.Id, provider.DisplayName, provider.NeedsApiKey, provider.NeedsNetwork);
}
