using System.Diagnostics;
using System.Text.Json;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;
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

/// <summary>优化方式下拉里的一项，显示中文名，取值仍是 <see cref="RefineMode"/>。</summary>
internal sealed record RefineModeChoice(RefineMode Mode)
{
    public override string ToString() => RefineModes.DisplayName(Mode);
}

/// <summary>
/// 设置窗口，分三块：常规（提示音、开机自启、热键）、语音识别（识别引擎、API Key）、
/// 结果优化（优化方式、独立 Key、模型、接口地址、句末标点）。其余字段是“调错就
/// 难查”的参数（重试、超时、退避、上限），留在配置文件里手改更合适。
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

    private readonly ComboBox _aiMode = new();
    private readonly CheckBox _keepTrailingPunctuation = new();
    private readonly TextBox _aiApiKey = new();
    private readonly ComboBox _aiModel = new();
    private readonly TextBox _aiBaseUrl = new();

    /// <summary>“不优化”时要整块变灰的那几项：Key、接口地址、模型。</summary>
    private readonly TableLayoutPanel _aiAdvanced = new();

    /// <summary>展开模型下拉时用它去拉接口地址上的可用模型；拉不到就保持内置候选。</summary>
    private readonly ModelCatalog _modelCatalog = new();

    private bool _fetchingModels;

    private readonly string _configPath;
    private readonly string _logDirectory;
    private readonly AppConfig _original;

    public SettingsForm(
        AppConfig config,
        string configPath,
        string logDirectory,
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

        var selectedMode = RefineModes.TryParse(config.AiOptimizeMode, out var mode) ? mode : RefineMode.None;

        // “不优化”是方式里的第一项，不再有单独的总开关：一项设置只表达一件事，
        // 界面上就不会出现“开关关着、方式却选着”的状态。
        _aiMode.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var item in RefineModes.All)
        {
            _aiMode.Items.Add(new RefineModeChoice(item));
        }

        _aiMode.SelectedIndex = RefineModes.All.ToList().IndexOf(selectedMode);
        _aiMode.Width = 200;
        _aiMode.Margin = new Padding(6, 4, 0, 4);

        _keepTrailingPunctuation.Text = "保留句末标点";
        _keepTrailingPunctuation.Checked = config.KeepTrailingPunctuation;
        _keepTrailingPunctuation.AutoSize = true;
        _keepTrailingPunctuation.Margin = new Padding(6, 6, 0, 6);

        _aiApiKey.UseSystemPasswordChar = true;
        _aiApiKey.Text = config.AiOptimizeApiKey;
        _aiApiKey.Width = 200;
        _aiApiKey.Margin = new Padding(6, 4, 0, 4);

        // 模型是下拉选择，但允许手输：换一个兼容端点时，模型名不一定在候选里。
        // 候选只有 BuiltInModels 这一处定义，失败回退与初始化共用，免得改一处漏一处。
        _aiModel.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var model in BuiltInModels)
        {
            _aiModel.Items.Add(model);
        }

        _aiModel.Text = config.AiOptimizeModel;
        _aiModel.Width = 260;
        _aiModel.Margin = new Padding(6, 4, 0, 4);
        _aiModel.DropDown += OnAiModelDropDown;

        // 地址比模型名长，给宽一点；AutoSize 的窗口会跟着撑开。
        _aiBaseUrl.Text = config.AiOptimizeBaseUrl;
        _aiBaseUrl.Width = 260;
        _aiBaseUrl.Margin = new Padding(6, 4, 0, 4);

        _aiAdvanced.ColumnCount = 2;
        _aiAdvanced.RowCount = 3;
        _aiAdvanced.AutoSize = true;
        _aiAdvanced.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _aiAdvanced.Dock = DockStyle.Fill;
        _aiAdvanced.Margin = new Padding(0);
        _aiAdvanced.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _aiAdvanced.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _aiAdvanced.Controls.Add(FieldLabel("API Key"), 0, 0);
        _aiAdvanced.Controls.Add(_aiApiKey, 1, 0);
        _aiAdvanced.Controls.Add(FieldLabel("接口地址"), 0, 1);
        _aiAdvanced.Controls.Add(_aiBaseUrl, 1, 1);
        _aiAdvanced.Controls.Add(FieldLabel("模型"), 0, 2);
        _aiAdvanced.Controls.Add(_aiModel, 1, 2);

        var optimizeLayout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        optimizeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optimizeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        optimizeLayout.Controls.Add(FieldLabel("优化方式"), 0, 0);
        optimizeLayout.Controls.Add(_aiMode, 1, 0);

        optimizeLayout.Controls.Add(_aiAdvanced, 0, 1);
        optimizeLayout.SetColumnSpan(_aiAdvanced, 2);

        optimizeLayout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
        optimizeLayout.Controls.Add(_keepTrailingPunctuation, 1, 2);

        var generalLayout = BuildTwoColumnLayout(3);
        generalLayout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 0);
        generalLayout.Controls.Add(_beep, 1, 0);
        generalLayout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 1);
        generalLayout.Controls.Add(_startup, 1, 1);
        generalLayout.Controls.Add(FieldLabel("热键"), 0, 2);
        generalLayout.Controls.Add(_hotkey, 1, 2);

        var speechLayout = BuildTwoColumnLayout(2);
        speechLayout.Controls.Add(FieldLabel("识别引擎"), 0, 0);
        speechLayout.Controls.Add(_provider, 1, 0);
        speechLayout.Controls.Add(FieldLabel("API Key"), 0, 1);
        speechLayout.Controls.Add(_apiKey, 1, 1);

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
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var generalGroup = MakeGroup("常规", generalLayout);
        var speechGroup = MakeGroup("语音识别", speechLayout);
        var optimizeGroup = MakeGroup("结果优化", optimizeLayout);

        layout.Controls.Add(generalGroup, 0, 0);
        layout.SetColumnSpan(generalGroup, 2);

        layout.Controls.Add(speechGroup, 0, 1);
        layout.SetColumnSpan(speechGroup, 2);

        layout.Controls.Add(optimizeGroup, 0, 2);
        layout.SetColumnSpan(optimizeGroup, 2);

        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;

        // 事件必须在控件都建好之后再挂：初始化时改 SelectedIndex 也会触发它。
        _aiMode.SelectedIndexChanged += (_, _) => ApplyModeState();
        ApplyModeState();
    }

    /// <summary>保存成功后带回新配置与开机自启开关，由装配层决定怎么应用。</summary>
    public event Action<SettingsResult>? Saved;

    private static TableLayoutPanel BuildTwoColumnLayout(int rows)
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return layout;
    }

    private static GroupBox MakeGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 14, 10),
            Margin = new Padding(0, 0, 0, 8),
        };
        group.Controls.Add(content);
        return group;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        // 三块各有自己的两列表格，列宽按内容算；不给一个统一下限，“热键”那行就会
        // 比“识别引擎”那行更窄，三块之间的输入框看着像没对齐。
        MinimumSize = new Size(64, 0),
        Margin = new Padding(0, 8, 8, 4),
    };

    /// <summary>
    /// 选了“不优化”就把 Key、模型、地址变灰：它们此刻用不上，留着可编辑只会让人
    /// 以为还在生效。句末标点不在其中，它与优化方式无关。
    /// </summary>
    private void ApplyModeState()
    {
        var mode = (_aiMode.SelectedItem as RefineModeChoice)?.Mode ?? RefineMode.None;
        _aiAdvanced.Enabled = mode != RefineMode.None;
    }

    /// <summary>
    /// 展开下拉时按当前接口地址与 Key 拉一次可用模型：模型名跟着端点走，写死一份候选
    /// 迟早过期。拉不到就退回内置候选并弹出原因——模型名本来也能手输。
    /// </summary>
    private async void OnAiModelDropDown(object? sender, EventArgs e)
    {
        if (_fetchingModels)
        {
            return;
        }

        var baseUrl = _aiBaseUrl.Text.Trim();
        var apiKey = _aiApiKey.Text.Trim();
        if (!AiEndpoint.IsAbsoluteHttpUrl(baseUrl) || apiKey.Length == 0)
        {
            return;
        }

        _fetchingModels = true;
        var current = _aiModel.Text;
        try
        {
            var models = await _modelCatalog.ListAsync(baseUrl, apiKey, ModelCatalog.DefaultTimeoutSeconds);

            if (IsDisposed)
            {
                return;
            }

            if (models.Count == 0)
            {
                // 空列表跟失败是一回事：下拉里没有可选项，用户得知道为什么。
                FailModelFetch("接口返回的模型列表是空的。", current);
                return;
            }

            var changed = _aiModel.Items.Count != models.Count
                || !_aiModel.Items.Cast<object>()
                    .Select(item => item?.ToString() ?? string.Empty)
                    .SequenceEqual(models, StringComparer.Ordinal);

            ReplaceModelItems(models, current);

            // 列表变了就重新展开一次，否则用户要点第二次才看得到新候选。
            if (changed && _aiModel.DroppedDown)
            {
                _aiModel.DroppedDown = false;
                _aiModel.DroppedDown = true;
            }
        }
        catch (Exception ex)
        {
            // 失败不再静默：用户点了下拉，就该知道是地址、Key 还是网络的问题。
            if (!IsDisposed)
            {
                FailModelFetch(DescribeFetchError(ex), current);
            }
        }
        finally
        {
            _fetchingModels = false;
        }
    }

    private static readonly string[] BuiltInModels = { AppConfig.DefaultAiModel, "deepseek-reasoner" };

    /// <summary>
    /// 失败时把候选退回内置两项再报错：对话框写的是“候选仍是内置的两项”，列表就必须真是。
    /// 收起下拉也在这里统一做，免得模态框压在悬停的列表上。
    /// </summary>
    private void FailModelFetch(string detail, string current)
    {
        _aiModel.DroppedDown = false;
        ReplaceModelItems(BuiltInModels, current);
        ShowModelFetchError(detail);
    }

    private void ReplaceModelItems(IReadOnlyList<string> models, string current)
    {
        _aiModel.BeginUpdate();
        try
        {
            _aiModel.Items.Clear();
            foreach (var model in models)
            {
                _aiModel.Items.Add(model);
            }
        }
        finally
        {
            _aiModel.EndUpdate();
        }

        // 换 Items 不该顺手动用户的文字：他可能正在手输。
        if (string.IsNullOrEmpty(_aiModel.Text))
        {
            _aiModel.Text = current;
        }
    }

    /// <summary>拉取模型失败时的提示，详细原因放进可展开区。</summary>
    private void ShowModelFetchError(string detail) =>
        Dialogs.Show(
            this,
            "无法获取模型列表",
            "候选仍是内置的两项，也可以直接手输模型名。",
            TaskDialogIcon.Error,
            detail);

    private static string DescribeFetchError(Exception error) => error switch
    {
        OperationCanceledException => "请求超过 5 秒没有返回。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
            "API Key 无效或已失效（HTTP 401）。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "接口地址上没有 /models 接口（HTTP 404）。",
        HttpRequestException { StatusCode: { } status } => $"接口返回 HTTP {(int)status}（{status}）。",
        HttpRequestException => "无法连接接口地址，请检查网络与地址。",
        JsonException => "接口返回的内容不是可识别的模型列表。",
        _ => "读取模型列表失败：" + error.Message,
    };

    private void OnSave(object? sender, EventArgs e)
    {
        var hotkey = _hotkey.SelectedItem as string ?? HotkeyNames.RightCtrl;
        var apiKey = _apiKey.Text.Trim();

        if (apiKey.Length > 0 && apiKey.Length < 16)
        {
            Dialogs.Show(this, "API Key 不完整", "请检查后再保存。", TaskDialogIcon.Warning);
            return;
        }

        var selectedMode = (_aiMode.SelectedItem as RefineModeChoice)?.Mode ?? RefineMode.None;
        var aiKey = _aiApiKey.Text.Trim();
        var aiModel = _aiModel.Text.Trim();
        var aiBaseUrl = _aiBaseUrl.Text.Trim();

        if (selectedMode == RefineMode.None)
        {
            // 选“不优化”时这三项是灰的，不能拿它们拦住保存；但配置里必须有能读回来的值，
            // 否则下次启动会报 E16，所以空值与非法值回落到默认。
            if (aiModel.Length is 0 or > 64)
            {
                aiModel = AppConfig.DefaultAiModel;
            }

            if (!AiEndpoint.IsAbsoluteHttpUrl(aiBaseUrl))
            {
                aiBaseUrl = AppConfig.DefaultBaseUrl;
            }
        }
        else
        {
            if (aiKey.Length > 0 && aiKey.Length < 16)
            {
                Dialogs.Show(this, "结果优化的 API Key 不完整", "请检查后再保存。", TaskDialogIcon.Warning);
                return;
            }

            if (aiModel.Length == 0)
            {
                Dialogs.Show(this, "模型名不能为空", "结果优化需要填写模型名。", TaskDialogIcon.Warning);
                return;
            }

            if (aiModel.Length > 64)
            {
                Dialogs.Show(this, "模型名过长", "结果优化的模型名最多 64 个字符。", TaskDialogIcon.Warning);
                return;
            }

            if (!AiEndpoint.IsAbsoluteHttpUrl(aiBaseUrl))
            {
                Dialogs.Show(
                    this, "接口地址不合法", "必须是完整的 http:// 或 https:// 地址。", TaskDialogIcon.Warning);
                return;
            }

            if (aiKey.Length == 0)
            {
                Dialogs.Show(
                    this,
                    "结果优化没有可用的 Key",
                    "已选择优化方式但没有填写 API Key，本次保存后听写会按原文粘贴。",
                    TaskDialogIcon.Warning);
            }
        }

        var updated = _original with
        {
            Hotkey = hotkey,
            Beep = _beep.Checked,
            ApiKey = apiKey,
            Provider = (_provider.SelectedItem as ProviderChoice)?.Descriptor.Id ?? _original.Provider,
            AiOptimizeApiKey = aiKey,
            AiOptimizeMode = RefineModes.ToConfigValue(selectedMode),
            KeepTrailingPunctuation = _keepTrailingPunctuation.Checked,
            AiOptimizeModel = aiModel,
            AiOptimizeBaseUrl = aiBaseUrl,
        };

        try
        {
            ConfigLoader.Save(_configPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Show(this, "无法写入配置文件", ex.Message, TaskDialogIcon.Error);
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
            Dialogs.Show(this, "无法打开日志目录", ex.Message, TaskDialogIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _modelCatalog.Dispose();
        }

        base.Dispose(disposing);
    }
}
