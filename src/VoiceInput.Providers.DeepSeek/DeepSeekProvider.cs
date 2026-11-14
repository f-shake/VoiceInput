using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Providers;

namespace VoiceInput.Providers.DeepSeek;

/// <summary>
/// DeepSeek 云端的插件入口。宿主只看这个类：它声明需要 Key、需要网络，
/// 然后按 options 里的 baseUrl 与宿主给的调优参数造出转写器。
/// </summary>
public sealed class DeepSeekProvider : IAsrProvider
{
    private const string DefaultBaseUrl = "https://api.deepseek.com";

    public string Id => "deepseek";

    public string DisplayName => "DeepSeek 云端";

    public bool NeedsApiKey => true;

    public bool NeedsNetwork => true;

    public ITranscriber Create(ProviderContext context) =>
        new DeepSeekTranscriber(
            context.ApiKey,
            context.Get("baseUrl", DefaultBaseUrl),
            context.Tuning);
}
