using System.Globalization;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Providers;

namespace VoiceInput.Providers.SenseVoice;

/// <summary>
/// SenseVoice 本地引擎入口。和云端引擎的本质区别写在两个标记上：不需要 Key、
/// 不需要网络——宿主据此决定要不要校验 Key、界面上要不要标“本地”。
/// </summary>
public sealed class SenseVoiceProvider : IAsrProvider
{
    private const string ModelFolder = "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17";

    public string Id => "sensevoice-local";

    public string DisplayName => "SenseVoice 本地（离线）";

    public bool NeedsApiKey => false;

    public bool NeedsNetwork => false;

    public ITranscriber Create(ProviderContext context)
    {
        var directory = context.Get("modelDir", DefaultModelDirectory());
        var threads = int.TryParse(context.Get("numThreads", "2"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, 16)
            : 2;

        return new SenseVoiceTranscriber(new SenseVoiceOptions(
            ModelDirectory: directory,
            ModelPath: context.Get("modelFile", Path.Combine(directory, "model.int8.onnx")),
            TokensPath: context.Get("tokensFile", Path.Combine(directory, "tokens.txt")),
            NumThreads: threads,
            Provider: context.Get("executionProvider", "cpu"),
            Language: context.Get("language", "auto"),
            UseInverseTextNormalization: !string.Equals(context.Get("useItn", "true"), "false", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>模型默认放在用户目录，和插件一起发布的是代码，不是几百兆的模型。</summary>
    private static string DefaultModelDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceInput",
        "models",
        ModelFolder);
}
