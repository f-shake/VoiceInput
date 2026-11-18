using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Config;

public sealed record ConfigLoadResult(
    AppConfig Config,
    IReadOnlyList<string> Warnings,
    string Path,
    bool Exists);

/// <summary>
/// 读配置、补默认、校验范围。校验放在这里而不是各使用点，是为了让写错的配置
/// 在启动时变成一条明确错误，而不是运行到一半才发现 maxSeconds 是个字符串。
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        // 手写配置文件里留注释和尾逗号很常见，为此报错只会让人改配置改得更烦。
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ConfigLoadResult Load(string path)    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ConfigLoadResult(new AppConfig(), Array.Empty<string>(), path, false);
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VoiceInputException(
                ErrorCodes.ConfigReadFailed, ErrorCatalog.ConfigReadFailed, detail: ex.Message, inner: ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new VoiceInputException(
                ErrorCodes.ConfigParseFailed,
                ErrorCatalog.ConfigParseFailed,
                detail: $"第 {ex.LineNumber + 1} 行附近有语法错误",
                inner: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("config", "顶层必须是 JSON 对象");
            }

            var config = new AppConfig();
            var unknown = new List<string>();

            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "apiKey":
                        config = config with { ApiKey = ReadString(property.Name, value) };
                        break;
                    case "hotkey":
                        config = config with { Hotkey = ReadHotkey(value) };
                        break;
                    case "deviceNumber":
                        config = config with { DeviceNumber = ReadNullableInt(property.Name, value) };
                        break;
                    case "maxSeconds":
                        config = config with { MaxSeconds = ReadInt(property.Name, value, 1, AppConfig.MaxSecondsLimit) };
                        break;
                    case "minSeconds":
                        config = config with { MinSeconds = ReadDouble(property.Name, value, 0, 10) };
                        break;
                    case "connectTimeoutSeconds":
                        config = config with { ConnectTimeoutSeconds = ReadDouble(property.Name, value, 0.5, 30) };
                        break;
                    case "readTimeoutSeconds":
                        config = config with { ReadTimeoutSeconds = ReadDouble(property.Name, value, 5, 120) };
                        break;
                    case "retries":
                        config = config with { Retries = ReadInt(property.Name, value, 0, 5) };
                        break;
                    case "retryBackoffSeconds":
                        config = config with { RetryBackoffSeconds = ReadDoubleArray(property.Name, value) };
                        break;
                    case "beep":
                        config = config with { Beep = ReadBool(property.Name, value) };
                        break;
                    case "pasteDelayMilliseconds":
                        config = config with { PasteDelayMilliseconds = ReadInt(property.Name, value, 0, 1000) };
                        break;
                    case "restoreClipboard":
                        config = config with { RestoreClipboard = ReadBool(property.Name, value) };
                        break;
                    case "overlay":
                        {
                            var overlay = ReadOverlay(property.Name, value);
                            config = config with { OverlayX = overlay.X, OverlayY = overlay.Y };
                            unknown.AddRange(overlay.Unknown.Select(field => $"overlay.{field}"));
                            break;
                        }
                    case "aiOptimizeApiKey":
                        config = config with { AiOptimizeApiKey = ReadString(property.Name, value) };
                        break;
                    case "aiOptimizeMode":
                        config = config with { AiOptimizeMode = ReadRefineMode(value) };
                        break;
                    case "keepTrailingPunctuation":
                        config = config with { KeepTrailingPunctuation = ReadBool(property.Name, value) };
                        break;
                    case "aiOptimizeModel":
                        config = config with { AiOptimizeModel = ReadAiModel(value) };
                        break;
                    case "aiOptimizeBaseUrl":
                        config = config with { AiOptimizeBaseUrl = ReadAiBaseUrl(value) };
                        break;
                    case "aiOptimizeTimeoutSeconds":
                        config = config with { AiOptimizeTimeoutSeconds = ReadDouble(property.Name, value, 1, 60) };
                        break;
                    case "aiOptimizeThinking":
                        config = config with { AiOptimizeThinking = ReadBool(property.Name, value) };
                        break;
                    case "provider":
                        config = config with { Provider = ReadProvider(value) };
                        break;
                    case "providerOptions":
                        config = config with { ProviderOptions = ReadOptions(property.Name, value) };
                        break;
                    default:
                        unknown.Add(property.Name);
                        break;
                }
            }

            if (config.MinSeconds >= config.MaxSeconds)
            {
                throw Invalid("minSeconds", "必须小于 maxSeconds");
            }

            if (config.RetryBackoffSeconds.Count < config.Retries)
            {
                throw Invalid("retryBackoffSeconds", $"至少要有 {config.Retries} 项");
            }

            var warnings = new List<string>();
            if (unknown.Count > 0)
            {
                warnings.Add("配置中有未识别的字段：" + string.Join("、", unknown) + "，已忽略。");
            }

            return new ConfigLoadResult(config, warnings, path, true);
        }
    }

    /// <summary>
    /// 把配置写回文件。先读旧文件、只覆盖我们认识的字段，其余原样保留：
    /// 用户手写的额外字段不该被设置窗口抹掉。写入先落临时文件再替换，避免断电时
    /// 留下半个配置文件。
    /// </summary>
    public static void Save(string path, AppConfig config)
    {
        var root = new JsonObject();
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path), nodeOptions: null, ParseOptions) is JsonObject existing)
                {
                    foreach (var pair in existing)
                    {
                        root[pair.Key] = pair.Value?.DeepClone();
                    }
                }
            }
            catch (JsonException)
            {
                // 旧文件已经坏了，直接按新配置重写。
            }
        }

        root["apiKey"] = config.ApiKey;
        root["hotkey"] = config.Hotkey;
        root["provider"] = config.Provider;
        root["providerOptions"] = new JsonObject(
            config.ProviderOptions.Select(pair =>
                KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))));
        if (config.DeviceNumber is null)
        {
            root["deviceNumber"] = null;
        }
        else
        {
            root["deviceNumber"] = config.DeviceNumber.Value;
        }

        root["maxSeconds"] = config.MaxSeconds;
        root["minSeconds"] = config.MinSeconds;
        root["connectTimeoutSeconds"] = config.ConnectTimeoutSeconds;
        root["readTimeoutSeconds"] = config.ReadTimeoutSeconds;
        root["retries"] = config.Retries;
        root["retryBackoffSeconds"] = new JsonArray(
            config.RetryBackoffSeconds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        root["beep"] = config.Beep;
        root["pasteDelayMilliseconds"] = config.PasteDelayMilliseconds;
        root["restoreClipboard"] = config.RestoreClipboard;
        // 只改 x/y，保留 overlay 里用户自己写的其它键：整个替换会把它们悄悄抹掉。
        var overlay = root["overlay"] as JsonObject ?? new JsonObject();
        overlay["x"] = Math.Round(config.OverlayX, 4);
        overlay["y"] = Math.Round(config.OverlayY, 4);
        root["overlay"] = overlay;
        root["aiOptimizeApiKey"] = config.AiOptimizeApiKey;
        root["aiOptimizeMode"] = config.AiOptimizeMode;
        root["keepTrailingPunctuation"] = config.KeepTrailingPunctuation;
        root["aiOptimizeModel"] = config.AiOptimizeModel;
        root["aiOptimizeBaseUrl"] = config.AiOptimizeBaseUrl;
        root["aiOptimizeTimeoutSeconds"] = config.AiOptimizeTimeoutSeconds;
        root["aiOptimizeThinking"] = config.AiOptimizeThinking;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// 按顺序找配置文件：显式路径 → exe 同目录 → 用户本地应用数据目录。
    /// 只返回路径，不判断存在性——最终用哪个路径要写进日志首行。
    /// </summary>
    public static string ResolvePath(string? explicitPath, string executableDirectory, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var beside = System.IO.Path.Combine(executableDirectory, "config.json");
        if (File.Exists(beside))
        {
            return beside;
        }

        return System.IO.Path.Combine(localAppData, "VoiceInput", "config.json");
    }

    private static VoiceInputException Invalid(string name, string detail) =>
        new(ErrorCodes.ConfigValueInvalid, ErrorCatalog.ConfigInvalid(name), detail: detail);

    private static VoiceInputException Invalid(string name, JsonElement value) =>
        Invalid(name, $"实际值 {value.GetRawText()}");

    private static string ReadString(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(name, value);
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>插件标识只允许安全字符：它会被当作文件与配置里的键使用。</summary>
    private static string ReadProvider(JsonElement value)
    {
        var raw = ReadString("provider", value).Trim();
        if (raw.Length == 0)
        {
            throw Invalid("provider", "不能为空");
        }

        if (!Regex.IsMatch(raw, "^[A-Za-z0-9._-]{1,40}$"))
        {
            throw Invalid("provider", "只允许字母、数字、点、下划线与连字符");
        }

        return raw.ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> ReadOptions(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(name, "必须是对象");
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                // 只收字符串：允许数字会让插件各自做类型猜测，配置一旦手写错就只能靠猜。
                throw Invalid(name, $"键 {property.Name} 的值必须是字符串");
            }

            options[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return options;
    }

    private static string ReadHotkey(JsonElement value)    {
        var raw = ReadString("hotkey", value);
        if (!HotkeyNames.TryNormalize(raw, out var normalized))
        {
            throw Invalid("hotkey", $"只接受右 Ctrl 与 F1~F12，实际值 {raw}");
        }

        return normalized;
    }

    /// <summary>
    /// 方式只收四个规范值：写错了就在启动时报出来，而不是悄悄退回默认值——
    /// 那会让人以为“选了却没生效”。
    /// </summary>
    private static string ReadRefineMode(JsonElement value)
    {
        var raw = ReadString("aiOptimizeMode", value);
        if (!RefineModes.TryParse(raw, out var mode))
        {
            throw Invalid(
                "aiOptimizeMode",
                $"只接受 {RefineModes.None}、{RefineModes.Polish}、{RefineModes.Fix}、{RefineModes.Punctuation}，实际值 {raw}");
        }

        return RefineModes.ToConfigValue(mode);
    }

    /// <summary>模型名可以为空：候选由端点决定，没选过时就是空的。</summary>
    private static string ReadAiModel(JsonElement value)
    {
        var raw = ReadString("aiOptimizeModel", value).Trim();
        return raw.Length <= 64 ? raw : throw Invalid("aiOptimizeModel", "最多 64 个字符");
    }

    /// <summary>
    /// 只接受绝对地址：相对地址只有在“程序恰好从某个目录启动”时才对，那种毛病
    /// 换个启动方式就复现不了，不如当场报出来。校验口径与设置窗口共用
    /// <see cref="AiEndpoint"/>，写进去的值必须读得回来。
    /// </summary>
    private static string ReadAiBaseUrl(JsonElement value)
    {
        var raw = ReadString("aiOptimizeBaseUrl", value).Trim();
        if (!AiEndpoint.IsAbsoluteHttpUrl(raw))
        {
            throw Invalid("aiOptimizeBaseUrl", $"必须是 http 或 https 开头的绝对地址，实际值 {raw}");
        }

        return raw;
    }

    private static int? ReadNullableInt(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(name, value);
        }

        return number >= 0 ? number : throw Invalid(name, "不能是负数");
    }

    private static int ReadInt(string name, JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(name, value);
        }

        return number >= min && number <= max
            ? number
            : throw Invalid(name, $"取值范围 {min} 到 {max}，实际值 {number}");
    }

    private static double ReadDouble(string name, JsonElement value, double min, double max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw Invalid(name, value);
        }

        return number >= min && number <= max
            ? number
            : throw Invalid(name, $"取值范围 {min} 到 {max}，实际值 {number}");
    }

    private static bool ReadBool(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid(name, value),
    };

    /// <summary>
    /// overlay 是唯一一个嵌套对象字段。越界数字钳回范围而不是报错：手改配置把 0.92
    /// 写成 9.2，只该让圆贴边，不该让整个程序起不来。
    /// </summary>
    private static (double X, double Y, List<string> Unknown) ReadOverlay(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(name, "必须是对象，形如 { \"x\": 0.5, \"y\": 0.92 }");
        }

        var x = AppConfig.DefaultOverlayX;
        var y = AppConfig.DefaultOverlayY;
        var unknown = new List<string>();
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "x":
                    x = ReadFraction($"{name}.x", property.Value);
                    break;
                case "y":
                    y = ReadFraction($"{name}.y", property.Value);
                    break;
                default:
                    // 拼错键名（比如写成 "X"）不该悄悄退回默认位置：和顶层未知字段一样进告警。
                    unknown.Add(property.Name);
                    break;
            }
        }

        return (x, y, unknown);
    }

    private static double ReadFraction(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw Invalid(name, value);
        }

        return Math.Clamp(number, AppConfig.OverlayMinFraction, AppConfig.OverlayMaxFraction);
    }

    private static IReadOnlyList<double> ReadDoubleArray(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(name, value);
        }

        var items = new List<double>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number))
            {
                throw Invalid(name, $"每一项都必须是数字，实际值 {item.GetRawText()}");
            }

            if (number < 0 || number > 10)
            {
                throw Invalid(name, $"每一项取值范围 0 到 10，实际值 {number}");
            }

            items.Add(number);
        }

        return items;
    }
}
