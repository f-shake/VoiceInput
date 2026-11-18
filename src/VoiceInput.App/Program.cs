using VoiceInput.App.Platform;
using VoiceInput.App.Providers;
using VoiceInput.App.Ui;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;
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
            Dialogs.Show(null, "已有实例在运行", ErrorCatalog.AlreadyRunning, TaskDialogIcon.Information);
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
            Dialogs.Show(null, "无法读取配置", ex.UserMessage, TaskDialogIcon.Error, ex.Detail);
            return ExitCodeStartupFailure;
        }

        var apiKey = config.ApiKey.Trim();
        log.Info(apiKey.Length > 0 ? "API Key：来自配置文件。" : "API Key：配置文件里没有填。");
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
        var logDirectory = Path.Combine(localAppData, "VoiceInput", "logs");
        if ((selected?.Instance.NeedsApiKey ?? true) && apiKey.Length == 0)
        {
            // 没有环境变量可退：缺 Key 时直接打开设置窗口让人填，否则程序起不来、也就没地方填。
            log.Warn($"{ErrorCodes.ApiKeyMissing} 未配置 API Key，打开设置窗口。");
            var startup = new StartupRegistration();
            if (PromptForApiKey(config, configPath, logDirectory, providers, startup, log))
            {
                try
                {
                    config = ConfigLoader.Load(configPath).Config;
                }
                catch (VoiceInputException ex)
                {
                    log.Error($"{ex.Code} {ex.UserMessage} {ex.Detail}");
                    Dialogs.Show(null, "无法读取配置", ex.UserMessage, TaskDialogIcon.Error, ex.Detail);
                    return ExitCodeStartupFailure;
                }

                apiKey = config.ApiKey.Trim();
                // 用户可能在窗口里换成了不需要 Key 的本地引擎，按新配置重新判断。
                selected = providers.Find(config.Provider);
            }

            if ((selected?.Instance.NeedsApiKey ?? true) && apiKey.Length == 0)
            {
                // 取消，或保存后仍是需要 Key 的引擎却没有 Key。
                log.Error($"{ErrorCodes.ApiKeyMissing} 启动时仍缺少可用的 Key，退出。");
                Dialogs.Show(null, "未配置 API Key", "请在设置里填写后再启动。", TaskDialogIcon.Warning);
                return ExitCodeStartupFailure;
            }
        }

        try
        {
            using var context = new TrayApplicationContext(
                config, apiKey, configPath, logDirectory, providers, log);
            Application.Run(context);
        }

        catch (Exception ex)
        {
            log.Error($"{ErrorCodes.Unexpected} 启动失败：{ex}");
            Dialogs.Show(null, "启动失败", ErrorCatalog.Unexpected, TaskDialogIcon.Error);
            return ExitCodeStartupFailure;
        }

        log.Info("已退出。");
        return 0;
    }

    /// <summary>
    /// 缺 Key 时的唯一出路：直接打开设置窗口让人填。Key 只从设置里来，没有环境变量
    /// 可退，再弹一句“请检查 config.json”只会让人去找一个不存在的入口。
    /// </summary>
    private static bool PromptForApiKey(
        AppConfig config,
        string configPath,
        string logDirectory,
        ProviderHost providers,
        StartupRegistration startup,
        IDiagnosticsLog log)
    {
        using var form = new SettingsForm(
            config,
            configPath,
            logDirectory,
            startup.IsEnabled,
            providers.Providers.Select(provider => provider.Descriptor).ToList());

        var saved = false;
        form.Saved += result =>
        {
            saved = true;
            // 首次启动这条路没有 ApplySettingsCore 兜底，开机自启得在这里落地。
            if (!startup.SetEnabled(result.StartupEnabled))
            {
                log.Warn($"{ErrorCodes.Unexpected} 开机自启写注册表失败，该项未生效。");
            }
            else
            {
                log.Info($"开机自启已{(result.StartupEnabled ? "启用" : "关闭")}：{startup.ExecutablePath}");
            }
        };

        form.ShowDialog();
        return saved;
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
    private readonly NaudioMicRecorder _recorder;
    private readonly string _configPath;
    private readonly string _logDirectory;
    private readonly IDiagnosticsLog _log;

    private AppConfig _config;
    private ITranscriber _transcriber;
    private ITextRefiner? _refiner;
    private string _hotkey;
    private string _currentKey;
    private SettingsForm? _settings;
    private volatile bool _settingsOpen;
    private bool _disposed;

    public TrayApplicationContext(
        AppConfig config,
        string apiKey,
        string configPath,
        string logDirectory,
        ProviderHost providers,
        IDiagnosticsLog log)
    {
        _log = log;
        _config = config;
        _hotkey = config.Hotkey;
        _configPath = configPath;
        _logDirectory = logDirectory;
        _currentKey = apiKey;
        _startup = new StartupRegistration();
        _providers = providers;
        // 录音器先建：状态条画波形要靠它每块算出来的音量。
        _recorder = new NaudioMicRecorder(config);

        try
        {
            _overlay = new StatusOverlay(log, config, () => _recorder.Level);
            // 先把窗口句柄建出来：剪贴板操作要往这个窗口上 Invoke，
            // 而且 TaskbarCreated 消息也只有已经建过句柄的窗口收得到。
            _ = _overlay.Handle;
            _status = _overlay;
            _invokeAnchor = _overlay;

            // 只在松手时写盘：拖动中每动一下就写会把配置文件刷爆。
            _overlay.Moved += SaveOverlayPosition;
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
        _refiner = BuildRefiner(config);

        _runner = new VoiceInputRunner(
            config,
            _recorder,
            _transcriber,
            new ClipboardPaster(config, _invokeAnchor),
            _status,
            new ToneFeedback(() => _config.Beep),
            log,
            _refiner,
            () => _config.KeepTrailingPunctuation);

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
            (_refiner as IDisposable)?.Dispose();
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
    /// 造结果优化器。选“不优化”时返回 null（状态机据此跳过这一步）；选了方式但没填
    /// Key 或没选模型时返回一个“一调用就失败”的占位优化器，让界面显示橙色失败提示、
    /// 日志写明原因——不能悄悄按“不优化”处理，否则用户以为优化生效了。
    /// </summary>
    private ITextRefiner? BuildRefiner(AppConfig config)
    {
        if (!RefineModes.TryParse(config.AiOptimizeMode, out var mode) || mode == RefineMode.None)
        {
            return null;
        }

        var description = RefineModes.ToConfigValue(mode);
        var apiKey = config.AiOptimizeApiKey.Trim();
        if (apiKey.Length == 0)
        {
            _log.Warn($"{ErrorCodes.AiOptimizeFailed} 结果优化已选择{RefineModes.DisplayName(mode)}，但没有填写 API Key。");
            return new UnavailableRefiner(description, "没有填写 API Key");
        }

        var model = config.AiOptimizeModel.Trim();
        if (model.Length == 0)
        {
            _log.Warn($"{ErrorCodes.AiOptimizeFailed} 结果优化已选择{RefineModes.DisplayName(mode)}，但没有选择模型。");
            return new UnavailableRefiner(description, "没有选择模型");
        }

        return new DeepSeekTextRefiner(
            apiKey,
            config.AiOptimizeBaseUrl,
            new RefineOptions(mode, model, config.AiOptimizeTimeoutSeconds, config.AiOptimizeThinking));
    }

    private static string DescribeOptimize(AppConfig config) =>
        RefineModes.TryParse(config.AiOptimizeMode, out var mode)
            ? RefineModes.DisplayName(mode)
            : config.AiOptimizeMode;

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
                // 按下这一刻还按着别的键，说明用户要打的是那个键的组合键，不是听写。
                _runner.HotkeyDown(_hook.ModifierKeysDownBesides(key.VirtualKey));
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
            Dialogs.Show(
                null,
                "设置未能完全应用",
                "设置已写入文件，但应用到运行中失败，重启程序后生效。",
                TaskDialogIcon.Warning,
                ex.Message);
        }
    }

    private void ApplySettingsCore(SettingsResult result)
    {
        var updated = result.Config;
        var previousConfig = _config;
        var hotkeyChanged = !string.Equals(updated.Hotkey, _hotkey, StringComparison.Ordinal);
        var providerChanged = !string.Equals(updated.Provider, _config.Provider, StringComparison.Ordinal);

        // 这几项都会进 RefineOptions 或被请求体消费，改了就重建实例：超时是构造时快照的，
        // 不重建的话设置里改了也不会生效；句末标点不属于优化器，改它不重建。
        var refinerChanged =
            !string.Equals(previousConfig.AiOptimizeMode, updated.AiOptimizeMode, StringComparison.Ordinal)
            || !string.Equals(previousConfig.AiOptimizeApiKey, updated.AiOptimizeApiKey, StringComparison.Ordinal)
            || !string.Equals(previousConfig.AiOptimizeModel, updated.AiOptimizeModel, StringComparison.Ordinal)
            || !string.Equals(previousConfig.AiOptimizeBaseUrl, updated.AiOptimizeBaseUrl, StringComparison.Ordinal)
            || previousConfig.AiOptimizeTimeoutSeconds != updated.AiOptimizeTimeoutSeconds
            || previousConfig.AiOptimizeThinking != updated.AiOptimizeThinking;

        _config = updated;
        _hotkey = updated.Hotkey;
        if (hotkeyChanged)
        {
            // 托盘悬停提示写着热键，改了就得刷，否则它一直是旧键。
            _tray?.RefreshHint();
        }

        if (!_startup.SetEnabled(result.StartupEnabled))
        {
            _log.Warn($"{ErrorCodes.Unexpected} 开机自启写注册表失败，该项未生效。");
        }
        else
        {
            _log.Info($"开机自启已{(result.StartupEnabled ? "启用" : "关闭")}：{_startup.ExecutablePath}");
        }

        // Key 只有一个来源：设置里填的值（写进配置文件的 aiKey 就是它自己）。
        var effectiveKey = updated.ApiKey.Trim();

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
                $"识别引擎已切换为 {updated.Provider}，Key {(effectiveKey.Length > 0 ? "已更新" : "已清空")}。");
        }

        if (refinerChanged)
        {
            var previousRefiner = _refiner;
            _refiner = BuildRefiner(updated);
            (previousRefiner as IDisposable)?.Dispose();
            _runner.ReplaceRefiner(_refiner);
            _log.Info($"结果优化已更新：{DescribeOptimize(updated)}。");
        }

        _log.Info(
            $"设置已保存：热键 {updated.Hotkey}（{(hotkeyChanged ? "已立即生效" : "未变")}），"
            + $"提示音 {updated.Beep}，开机自启 {result.StartupEnabled}，引擎 {updated.Provider}，"
            + $"结果优化 {DescribeOptimize(updated)}。");
    }

    /// <summary>
    /// 拖动结束后落盘。值没变就什么都不做，免得“点一下再放开”也重写一次配置文件。
    /// </summary>
    private void SaveOverlayPosition(double x, double y)
    {
        if (Math.Abs(x - _config.OverlayX) < 0.0005 && Math.Abs(y - _config.OverlayY) < 0.0005)
        {
            return;
        }

        _config = _config with { OverlayX = x, OverlayY = y };
        try
        {
            ConfigLoader.Save(_configPath, _config);
            _log.Info($"状态条位置已保存：主屏宽 {x:P1}、高 {y:P1} 处。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{ErrorCodes.Unexpected} 状态条位置保存失败：{ex.Message}");
        }
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
