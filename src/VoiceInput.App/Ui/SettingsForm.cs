using System.Diagnostics;
using System.Globalization;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Providers;

namespace VoiceInput.App.Ui;

/// <summary>设置窗口的保存结果：配置本身加一个不写进配置文件的开关。</summary>
public readonly record struct SettingsResult(AppConfig Config, bool StartupEnabled);

/// <summary>下拉里的一项。ComboBox 显示的是 ToString 的结果。</summary>
internal sealed record ProviderChoice(ProviderDescriptor Descriptor)
{
    public override string ToString() =>
        Descriptor.NeedsNetwork ? Descriptor.DisplayName : Descriptor.DisplayName + "（本地）";
}

/// <summary>
/// 设置窗口：识别引擎、热键、提示音、开机自启、API Key 五项。其余十个字段是
/// “调错就难查”的参数（重试、超时、退避、上限），留在配置文件里手改更合适。
/// </summary>
/// <remarks>
/// 布局一律不用绝对坐标：<see cref="AutoScaleMode.Font"/> 让控件随系统字体与 DPI
/// 缩放，<see cref="TableLayoutPanel"/> 按内容量算宽度。写死像素在 200% 缩放下
/// 会把标签和输入框压在一起，这是踩过的坑。
/// </remarks>
public sealed class SettingsForm : Form
{
    // 必须覆盖 HotkeyNames 能接受的全部键：下拉里没有的键会在保存时被悄悄改成右 Ctrl。
    private static readonly string[] HotkeyChoices = BuildHotkeyChoices();

    private static string[] BuildHotkeyChoices()
    {
        var choices = new List<string> { HotkeyNames.RightCtrl };
        for (var index = 1; index <= 12; index++)
        {
            choices.Add("F" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return choices.ToArray();
    }

    private readonly ComboBox _hotkey = new();
    private readonly ComboBox _provider = new();
    private readonly CheckBox _beep = new();
    private readonly CheckBox _startup = new();
    private readonly TextBox _apiKey = new();
    private readonly Label _keySource = new();
    private readonly string _configPath;
    private readonly string _logDirectory;
    private readonly AppConfig _original;

    public SettingsForm(
        AppConfig config,
        string configPath,
        string logDirectory,
        string keySourceText,
        bool startupEnabled,
        IReadOnlyList<ProviderDescriptor> providers)
    {
        _original = config;
        _configPath = configPath;
        _logDirectory = logDirectory;

        Text = "VoiceInput 设置";
        Icon = AppIcon.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _hotkey.DropDownStyle = ComboBoxStyle.DropDownList;
        _hotkey.Items.AddRange(HotkeyChoices);
        _hotkey.SelectedItem = Array.IndexOf(HotkeyChoices, config.Hotkey) >= 0
            ? config.Hotkey
            : HotkeyChoices[0];
        _hotkey.Width = 200;
        _hotkey.Margin = new Padding(6, 4, 0, 4);

        // 只有一个引擎时下拉也没得选，直接显示名字，省得让人以为还有别的没装。
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        if (providers.Count > 1)
        {
            foreach (var descriptor in providers)
            {
                _provider.Items.Add(new ProviderChoice(descriptor));
            }

            _provider.SelectedIndex = Math.Max(0, providers.ToList().FindIndex(
                descriptor => string.Equals(descriptor.Id, config.Provider, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            var only = providers.FirstOrDefault();
            _provider.Items.Add(new ProviderChoice(only ?? new ProviderDescriptor("deepseek", "DeepSeek 云端", true, true)));
            _provider.SelectedIndex = 0;
            _provider.Enabled = false;
        }

        _provider.Width = 200;
        _provider.Margin = new Padding(6, 4, 0, 4);

        _beep.Text = "开启提示音";
        _beep.Checked = config.Beep;
        _beep.AutoSize = true;
        _beep.Margin = new Padding(6, 6, 0, 6);

        _startup.Text = "开机自启（当前用户）";
        _startup.Checked = startupEnabled;
        _startup.AutoSize = true;
        _startup.Margin = new Padding(6, 6, 0, 6);

        _apiKey.UseSystemPasswordChar = true;
        _apiKey.Text = config.ApiKey;
        _apiKey.Width = 200;
        _apiKey.Margin = new Padding(6, 4, 0, 4);

        _keySource.Text = keySourceText;
        _keySource.ForeColor = SystemColors.GrayText;
        _keySource.AutoSize = true;
        // 文字要能换行，否则长句子会把窗口撑得很宽。
        _keySource.MaximumSize = new Size(320, 0);
        _keySource.Margin = new Padding(0, 2, 0, 8);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
            WrapContents = false,
        };

        var save = new Button { Text = "保存", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        save.Click += OnSave;
        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            DialogResult = DialogResult.Cancel,
        };
        var openLogs = new Button { Text = "打开日志", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        openLogs.Click += (_, _) => OpenLogDirectory();

        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(openLogs);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(FieldLabel("识别引擎"), 0, 0);
        layout.Controls.Add(_provider, 1, 0);

        layout.Controls.Add(FieldLabel("热键"), 0, 1);
        layout.Controls.Add(_hotkey, 1, 1);

        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
        layout.Controls.Add(_beep, 1, 2);

        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 3);
        layout.Controls.Add(_startup, 1, 3);

        layout.Controls.Add(FieldLabel("API Key"), 0, 4);
        layout.Controls.Add(_apiKey, 1, 4);

        layout.Controls.Add(_keySource, 0, 5);
        layout.SetColumnSpan(_keySource, 2);

        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;
    }

    /// <summary>保存成功后带回新配置与开机自启开关，由装配层决定怎么应用。</summary>
    public event Action<SettingsResult>? Saved;

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 8, 8, 4),
    };

    private void OnSave(object? sender, EventArgs e)
    {
        var hotkey = _hotkey.SelectedItem as string ?? HotkeyNames.RightCtrl;
        var apiKey = _apiKey.Text.Trim();

        if (apiKey.Length > 0 && apiKey.Length < 16)
        {
            MessageBox.Show(
                this, "API Key 看起来不完整，请检查后再保存。", "VoiceInput",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var updated = _original with
        {
            Hotkey = hotkey,
            Beep = _beep.Checked,
            ApiKey = apiKey,
            Provider = (_provider.SelectedItem as ProviderChoice)?.Descriptor.Id ?? _original.Provider,
        };

        try
        {
            ConfigLoader.Save(_configPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                string.Format(CultureInfo.InvariantCulture, "无法写入配置文件：{0}", ex.Message),
                "VoiceInput",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Saved?.Invoke(new SettingsResult(updated, _startup.Checked));
        Close();
    }

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this, "无法打开日志目录：" + ex.Message, "VoiceInput",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
