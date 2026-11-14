namespace VoiceInput.Core.Config;

public sealed record ApiKeyResult(string Key, string Source)
{
    public bool Found => !string.IsNullOrEmpty(Key);
}

/// <summary>
/// 按优先级找 API Key。设置里填的值排第一，环境变量退到第二位：用户在设置窗口
/// 亲手填的 Key 必须立刻生效，否则“填了却没反应”会变成一个查半天的怪现象。
/// 想回到环境变量，把设置里的 Key 清空即可。
///
/// 用户级环境变量由调用方以委托传入，因为读注册表是 Windows 平台细节，Core 只
/// 负责顺序与文案，这样整段逻辑可以在没有注册表的环境下测。
/// </summary>
public static class ApiKeyResolver
{
    public const string SourceFromSettings = "设置";
    public const string SourceFromConfigFile = "配置文件";

    public static readonly string[] EnvironmentVariableNames =
    {
        "VOICEINPUT_API_KEY",
        "DEEPSEEK_API_KEY",
    };

    public static ApiKeyResult Resolve(
        string? configKey,
        Func<string, string?>? processEnvironment = null,
        Func<string, string?>? userEnvironment = null)
    {
        var fromSettings = configKey?.Trim();
        if (!string.IsNullOrEmpty(fromSettings))
        {
            return new ApiKeyResult(fromSettings, SourceFromSettings);
        }

        var fromProcess = processEnvironment ?? Environment.GetEnvironmentVariable;
        foreach (var name in EnvironmentVariableNames)
        {
            var value = fromProcess(name)?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return new ApiKeyResult(value, $"环境变量 {name}");
            }
        }

        if (userEnvironment is not null)
        {
            foreach (var name in EnvironmentVariableNames)
            {
                var value = userEnvironment(name)?.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    return new ApiKeyResult(value, $"用户环境变量 {name}");
                }
            }
        }

        return new ApiKeyResult(string.Empty, string.Empty);
    }
}
