namespace VoiceInput.Core.Config;

/// <summary>
/// 结果优化接口地址的唯一校验口径。设置窗口与 <see cref="ConfigLoader"/> 必须用同一套：
/// 只查前缀会让 “https://” 这种值写进配置文件，下次启动直接读不回来。
/// </summary>
public static class AiEndpoint
{
    public static bool IsAbsoluteHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
