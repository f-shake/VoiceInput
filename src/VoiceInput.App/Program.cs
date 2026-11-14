using System.Security;
using Microsoft.Win32;
using VoiceInput.App.Platform;
using VoiceInput.App.Providers;
using VoiceInput.App.Ui;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Providers;

namespace VoiceInput.App;


internal static class Program
{
    private const string MutexName = @"Local\VoiceInput.SingleInstance";
    private const int ExitCodeStartupFailure = 2;

    [STAThread]
    private static int Main()
    {
        // 两个实例会各装一个键盘钩子，一次按键录两遍、粘两遍。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                ErrorCatalog.AlreadyRunning, "VoiceInput", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return ExitCodeStartupFailure;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var log = new RotatingFileLog(Path.Combine(localAppData, "VoiceInput", "logs"));

        // 后台线程上的未处理异常会直接杀掉进程，界面上一句话都不会留，
        // 所以先把最后一条线索写进日志，否则现场只剩一条 WER 记录。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error($"{ErrorCodes.Unexpected} 未处理异常：{args.ExceptionObject}");

        var configPath = ConfigLoader.ResolvePath(
            Environment.GetEnvironmentVariable("VOICEINPUT_CONFIG"),
            AppContext.BaseDirectory,
            localAppData);
        log.Info($"启动，配置文件：{configPath}");

        AppConfig config;
        try
        {
            var loaded = ConfigLoader.Load(configPath);
            config = loaded.Config;
            foreach (var warning in loaded.Warnings)
            {
                log.Warn(warning);
            }
        }
        catch (VoiceInputException ex)
        {
            log.Error($"{ex.Code} {ex.UserMessage} {ex.Detail}");
            MessageBox.Show(ex.UserMessage, "VoiceInput", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ExitCodeStartupFailure;
        }

        var apiKey = ApiKeyResolver.Resolve(config.ApiKey, userEnvironment: ReadUserEnvironment);
        log.Info(apiKey.Found
            ? $"API Key 来源：{apiKey.Source}。"
            : "API Key：进程环境变量、用户环境变量、配置文件都没有。");
        foreach (var screen in Screen.AllScreens)
        {
            log.Info($"显示器 {screen.DeviceName} 主屏={screen.Primary} 工作区={screen.WorkingArea}");
        }
        foreach (var device in NaudioMicRecorder.DescribeDevices())
        {
            log.Info($"输入设备 {device}");
        }

        // 两个插件目录：程序旁边的随包插件，以及用户目录里的自装插件。
        var providers = ProviderHost.Discover(
            new[]
            {
                Path.Combine(AppContext.BaseDirectory, "providers"),
                Path.Combine(localAppData, "VoiceInput", "providers"),
            },
            log);

        // 只有需要 Key 的引擎才拦启动：本地离线引擎没有 Key 也应该能起来。
        var selected = providers.Find(config.Provider);
        if ((selected?.Instance.NeedsApiKey ?? true) && !apiKey.Found)
        {
            log.Error($"{ErrorCodes.ApiKeyMissing} 选中的引擎需要 Key，但三个来源都没有。");
            MessageBox.Show(
                ErrorCatalog.ApiKeyMissing, "VoiceInput", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitCodeStartupFailure;
        }

        try
        {
            var logDirectory = Path.Combine(localAppData, "VoiceInput", "logs");
            var keySource = apiKey.Found ? apiKey.Source : "未配置";

            using var context = new TrayApplicationContext(
                config, apiKey.Key, configPath, logDirectory, keySource, ReadUserEnvironment, providers, log);
            Application.Run(context);
        }

        catch (Exception ex)
        {
            log.Error($"{ErrorCodes.Unexpected} 启动失败：{ex}");
            MessageBox.Show(
                ErrorCatalog.Unexpected, "VoiceInput", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ExitCodeStartupFailure;
        }

        log.Info("已退出。");
        return 0;
    }

    /// <summary>
    /// 读用户级环境变量。只读进程环境变量不够：新设的用户级变量不会补进已经在
    /// 运行的进程，现象是“明明设了却报没配置”。
    /// </summary>
    private static string? ReadUserEnvironment(string name)
    {
        try
        {
            using var environment = Registry.CurrentUser.OpenSubKey("Environment");
            return environment?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}

/// <summary>状态条建不出来时的空视图：什么都不显示，但让状态机照常跑完。</summary>
internal sealed class SilentStatusView : IStatusView
{
    public void Show(OverlayState state, string text)
    {
        // 故意留空：此时日志与提示音是仅有的两条反馈通道。
    }
}

/// <summary>
/// 托盘应用的生命周期：装配各层、把按键事件分派给状态机、按托盘菜单或
/// Ctrl+Alt+Q 退出。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int VkQ = 0x51;

    private readonly StatusOverlay? _overlay;
    private readonly IStatusView _status;
    private readonly Control _invokeAnchor;
    private readonly TrayIcon? _tray;
    private readonly KeyboardHook _hook;
    private readonly VoiceInputRunner _runner;
    private readonly StartupRegistration _startup;
    private readonly ProviderHost _providers;
    private readonly string _configPath;
    private readonly string _logDirectory;
    private readonly Func<string, string?> _readUserEnvironment;
    private readonly IDiagnosticsLog _log;

    private AppConfig _config;
    private ITranscriber _transcriber;
    private string _hotkey;
    private string _keySource;
    private string _currentKey;
    private SettingsForm? _settings;
    private volatile bool _settingsOpen;
    private bool _disposed;

    public TrayApplicationContext(
        AppConfig config,
        string apiKey,
        string configPath,
        string logDirectory,
        string keySource,
        Func<string, string?> readUserEnvironment,
        ProviderHost providers,
        IDiagnosticsLog log)
    {
        _log = log;
        _config = config;
        _hotkey = config.Hotkey;
        _configPath = configPath;
        _logDirectory = logDirectory;
        _keySource = keySource;
        _currentKey = apiKey;
        _readUserEnvironment = readUserEnvironment;
        _startup = new StartupRegistration();
        _providers = providers;

        try
        {
            _overlay = new StatusOverlay(log, config);
            // 先把窗口句柄建出来：剪贴板操作要往这个窗口上 Invoke，
            // 而且 TaskbarCreated 消息也只有已经建过句柄的窗口收得到。
            _ = _overlay.Handle;
            _status = _overlay;
            _invokeAnchor = _overlay;
        }
        catch (Exception ex)
        {
            // 状态条只是提示，不该让听写跟着不可用：躲到一个永不显示的空窗口上，
            // 提示音、日志、粘贴照旧工作（计划 F20 的降级路径）。
            log.Error($"{ErrorCodes.OverlayFailed} 状态条创建失败：{ex}");
            _overlay = null;
            _status = new SilentStatusView();
            _invokeAnchor = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            _ = _invokeAnchor.Handle;
        }

        try
        {
            _tray = new TrayIcon(() => _hotkey, OpenSettings, ExitFromTray);
            if (_overlay is not null)
            {
                _overlay.TaskbarCreated += _tray.Restore;
            }

            log.Info($"托盘图标绘制尺寸 {_tray.IconSize} 像素。");
        }
        catch (Exception ex)
        {
            // 托盘只是退出入口。它挂了还有 Ctrl+Alt+Q，听写不该跟着不可用。
            log.Error($"{ErrorCodes.TrayFailed} 托盘创建失败：{ex}");
            _tray = null;
        }

        _transcriber = BuildTranscriber(config, apiKey);

        _runner = new VoiceInputRunner(
            config,
            new NaudioMicRecorder(config),
            _transcriber,
            new ClipboardPaster(config, _invokeAnchor),
            _status,
            new ToneFeedback(() => _config.Beep),
            log);

        _hook = new KeyboardHook(message => _log.Warn(message));
        _hook.Key += OnKey;
        _hook.Install();
        log.Info($"就绪，按住 {config.Hotkey} 说话，松开转写。");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hook.Key -= OnKey;
            _hook.Dispose();
            (_transcriber as IDisposable)?.Dispose();
            _tray?.Dispose();

            // 降级时 _invokeAnchor 不是状态条，两处都要放掉。
            _overlay?.Dispose();
            if (!ReferenceEquals(_invokeAnchor, _overlay))
            {
                _invokeAnchor.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>按当前配置与 Key 从插件里造一个转写器。</summary>
    private ITranscriber BuildTranscriber(AppConfig config, string apiKey)
    {
        var tuning = new AsrTuning(
            config.Retries,
            config.RetryBackoffSeconds,
            config.ConnectTimeoutSeconds,
            config.ReadTimeoutSeconds);
        var context = new ProviderContext(apiKey, tuning, config.ProviderOptions);
        return _providers.Create(config.Provider, context, _log);
    }

    /// <summary>
    /// 钩子回调在自己的线程上，这里只能做判定和投递：录音与转写都是异步的，
    /// 任何阻塞都会让后面的按键全部堵住。
    /// </summary>
    private void OnKey(KeyEvent key)
    {
        // 兜底退出优先于一切：设置窗口开着时也得能退出，否则进程跑不掉。
        if (key.IsDown && key.Ctrl && key.Alt && key.VirtualKey == VkQ)
        {
            ExitFromTray();
            return;
        }

        // 设置窗口开着时不吃热键：否则在 API Key 输入框里说一句话，
        // 转写结果会被粘进输入框，保存后变成一把错误的 Key。
        if (_settingsOpen)
        {
            return;
        }

        if (string.Equals(key.Name, _hotkey, StringComparison.Ordinal))
        {
            if (key.IsDown)
            {
                _runner.HotkeyDown();
            }
            else
            {
                _ = _runner.HotkeyUpAsync();
            }

            return;
        }

        if (key.IsDown)
        {
            _runner.NoteOtherKey(key.Name);
        }
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        // 窗口每次重建：省掉“改完再改回去”时残留的旧控件状态。
        _settings = new SettingsForm(
            _config,
            _configPath,
            _logDirectory,
            _keySource,
            _startup.IsEnabled,
            _providers.Providers.Select(provider => provider.Descriptor).ToList());
        _settings.Saved += ApplySettings;
        _settings.FormClosed += (_, _) => _settings = null;

        _settingsOpen = true;
        try
        {
            _settings.ShowDialog();
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    /// <summary>
    /// 保存后的落地：能立即生效的立即生效，其余交给下一次录音或下一次请求。
    /// </summary>
    private void ApplySettings(SettingsResult result)
    {
        // 设置窗口里点一次保存不该让整个进程挂掉：切引擎要加载插件的依赖，
        // 任何一处失败都只该表现为一条提示，而不是崩溃对话框。
        try
        {
            ApplySettingsCore(result);
        }
        catch (Exception ex)
        {
            _log.Error($"{ErrorCodes.Unexpected} 应用设置失败：{ex}");
            MessageBox.Show(
                "设置已写入文件，但应用到运行中失败，重启程序后生效：" + ex.Message,
                "VoiceInput",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplySettingsCore(SettingsResult result)
    {
        var updated = result.Config;
        var hotkeyChanged = !string.Equals(updated.Hotkey, _hotkey, StringComparison.Ordinal);
        var providerChanged = !string.Equals(updated.Provider, _config.Provider, StringComparison.Ordinal);

        _config = updated;
        _hotkey = updated.Hotkey;

        if (!_startup.SetEnabled(result.StartupEnabled))
        {
            _log.Warn($"{ErrorCodes.Unexpected} 开机自启写注册表失败，该项未生效。");
        }
        else
        {
            _log.Info($"开机自启已{(result.StartupEnabled ? "启用" : "关闭")}：{_startup.ExecutablePath}");
        }

        // Key 的优先级是“设置里填的最优先”，所以这里重新解析一次：
        // 清空输入框时会自然回退到环境变量。
        var effective = ApiKeyResolver.Resolve(updated.ApiKey, userEnvironment: _readUserEnvironment);
        _keySource = effective.Found ? effective.Source : "未配置";
        var effectiveKey = effective.Found ? effective.Key : string.Empty;

        // 清空 Key 也必须算一次变化：否则旧 Key 继续在用，而界面显示“已清空”。
        var keyChanged = !string.Equals(effectiveKey, _currentKey, StringComparison.Ordinal);
        if (keyChanged || providerChanged)
        {
            var previous = _transcriber;
            _transcriber = BuildTranscriber(updated, effectiveKey);
            (previous as IDisposable)?.Dispose();
            _currentKey = effectiveKey;
            _runner.ReplaceTranscriber(_transcriber);
            _log.Info(
                $"识别引擎已切换为 {updated.Provider}，API Key 来源 {_keySource}。");
        }

        _log.Info(
            $"设置已保存：热键 {updated.Hotkey}（{(hotkeyChanged ? "已立即生效" : "未变")}），"
            + $"提示音 {updated.Beep}，开机自启 {result.StartupEnabled}，引擎 {updated.Provider}。");
    }

    private void ExitFromTray()
    {
        if (_invokeAnchor.IsDisposed)
        {
            ExitThread();
            return;
        }

        if (_invokeAnchor.InvokeRequired)
        {
            try
            {
                _invokeAnchor.BeginInvoke(new Action(ExitThread));
            }
            catch (InvalidOperationException)
            {
                _log.Warn("退出请求到达时窗口已销毁，直接结束进程。");
                ExitThread();
            }

            return;
        }

        ExitThread();
    }
}
