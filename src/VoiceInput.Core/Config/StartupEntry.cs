namespace VoiceInput.Core.Config;

/// <summary>
/// 开机自启那一条注册表值的格式与比对规则。写的是
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>，只影响当前用户，
/// 不需要管理员权限，也不进服务。
///
/// 放在 Core 是为了它能被单测覆盖：路径里带空格时必须加引号，否则 Windows 会把
/// 空格前那段当成可执行文件，开机直接报“找不到”。
/// </summary>
public static class StartupEntry
{
    public const string ValueName = "VoiceInput";

    public static string Format(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return "\"" + executablePath.Trim() + "\"";
    }

    /// <summary>
    /// 注册表里那条值是否指向这个可执行文件。比较时忽略大小写与引号差异：
    /// 用户可能手工改过，或者程序被移动过（那就应当视为没启用）。
    /// </summary>
    public static bool Matches(string? registryValue, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(registryValue) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        static string Normalize(string value) =>
            value.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

        return string.Equals(Normalize(registryValue), Normalize(executablePath), StringComparison.OrdinalIgnoreCase);
    }
}
