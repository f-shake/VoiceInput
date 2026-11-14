using System.Security;
using Microsoft.Win32;
using VoiceInput.Core.Config;

namespace VoiceInput.App.Platform;

/// <summary>
/// 开机自启：写当前用户的 Run 键，不需要管理员权限，也不进服务。
/// 只影响登录这个账户时是否启动本程序。
/// </summary>
public sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executablePath;

    public StartupRegistration(string? executablePath = null)
    {
        _executablePath = executablePath
            ?? Environment.ProcessPath
            ?? Application.ExecutablePath;
    }

    public string ExecutablePath => _executablePath;

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return StartupEntry.Matches(run?.GetValue(StartupEntry.ValueName) as string, _executablePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>写或删那条注册表值；返回是否成功，失败原因由调用方记日志。</summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (run is null)
            {
                return false;
            }

            if (enabled)
            {
                run.SetValue(
                    StartupEntry.ValueName,
                    StartupEntry.Format(_executablePath),
                    RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(StartupEntry.ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
