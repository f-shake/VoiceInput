using System.Globalization;
using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Config;

/// <summary>
/// 配置内容。20 个字段全部可选，缺省值就是可用值，所以“没有配置文件”也能启动到
/// 报缺 Key 那一步，而不是先炸在空引用上。
/// </summary>
/// <remarks>
/// RetryBackoffSeconds 是集合，record 自动生成的相等性比较的是引用，所以两份内容
/// 相同的配置并不相等。没有生产代码依赖配置之间的相等判断，因此保留默认语义，
/// 只在这里说清楚，免得日后有人拿 == 去比较两份配置。
/// </remarks>
public sealed record AppConfig
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultProvider = "deepseek";
    public const string DefaultAiModel = "deepseek-chat";
    public const int MaxSecondsLimit = 118;

    private static readonly IReadOnlyDictionary<string, string> DefaultOptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["baseUrl"] = DefaultBaseUrl,
        };

    public string ApiKey { get; init; } = string.Empty;

    /// <summary>用哪个识别引擎，对应 providers 目录里某个插件的 Id。</summary>
    public string Provider { get; init; } = DefaultProvider;

    /// <summary>
    /// 交给插件的自由键值：baseUrl、model、modelPath 之类。做成字符串字典而不是
    /// 给每家加字段，是为了加一个引擎时不用改配置结构，也不用改这个类。
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } = DefaultOptions;

    public string Hotkey { get; init; } = HotkeyNames.RightCtrl;

    public int? DeviceNumber { get; init; }

    public int MaxSeconds { get; init; } = MaxSecondsLimit;

    public double MinSeconds { get; init; } = 0.3;

    public double ConnectTimeoutSeconds { get; init; } = 5.0;

    public double ReadTimeoutSeconds { get; init; } = 60.0;

    public int Retries { get; init; } = 2;

    public IReadOnlyList<double> RetryBackoffSeconds { get; init; } = new[] { 0.5, 1.5 };

    public bool Beep { get; init; } = true;

    public int PasteDelayMilliseconds { get; init; } = 80;

    public bool RestoreClipboard { get; init; } = true;

    /// <summary>
    /// 结果优化专用的 Key，与语音转写的 Key 分开：转写换一家时，优化不该跟着失效。
    /// </summary>
    public string AiOptimizeApiKey { get; init; } = string.Empty;

    /// <summary>
    /// 优化方式，取值见 <see cref="RefineModes"/>。默认“不优化”：升级到一个带这项
    /// 功能的版本时，原有听写行为必须一个字节都不变，是否多花一次请求要用户自己选。
    /// </summary>
    public string AiOptimizeMode { get; init; } = RefineModes.None;

    /// <summary>
    /// 是否保留句末标点。这一项独立于总开关：关掉它时即使没开 AI 优化，也照样
    /// 去掉原文末尾的标点，因为那一步不需要请求。
    /// </summary>
    public bool KeepTrailingPunctuation { get; init; } = true;

    public string AiOptimizeModel { get; init; } = DefaultAiModel;

    public string AiOptimizeBaseUrl { get; init; } = DefaultBaseUrl;

    public double AiOptimizeTimeoutSeconds { get; init; } = 8.0;
}


/// <summary>
/// 热键别名表。只接受右 Ctrl 与 F1~F12：右 Ctrl 单独按下不产生字符，也不会和
/// 左 Ctrl 起手的组合键混淆；F 系键同理，且很少出现在组合键里。
/// 别名在此收敛成规范名，App 侧只认规范名，免得两处各写一份散落的字符串比较。
/// </summary>
public static class HotkeyNames
{
    public const string RightCtrl = "RightCtrl";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rightctrl"] = RightCtrl,
        ["rctrl"] = RightCtrl,
        ["right ctrl"] = RightCtrl,
    };

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var key = raw.Trim();
        if (Aliases.TryGetValue(key, out var canonical))
        {
            normalized = canonical;
            return true;
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'f' or 'F'))
        {
            var digits = key.AsSpan(1);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number is >= 1 and <= 12)
            {
                normalized = "F" + number.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        return false;
    }
}
