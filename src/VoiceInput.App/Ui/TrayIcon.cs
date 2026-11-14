namespace VoiceInput.App.Ui;

/// <summary>
/// 托盘图标。它是程序唯一常驻可见的东西，也是唯一可靠的退出入口——状态圆是鼠标
/// 穿透的，点不到。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    /// <summary>托盘图标实际用的像素尺寸，写日志用：模糊的根因通常就是这里和 DPI 对不上。</summary>
    public int IconSize { get; }

    public TrayIcon(Func<string> hotkey, Action openSettings, Action exitRequested)
    {
        IconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
        _icon = AppIcon.Load(IconSize);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("设置", null, (_, _) => openSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exitRequested());

        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "VoiceInput",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notify.DoubleClick += (_, _) => _notify.ShowBalloonTip(
            2000,
            "VoiceInput",
            $"按住 {hotkey()} 说话，松开转写。",
            ToolTipIcon.Info);
    }

    /// <summary>资源管理器重启后托盘区被重建，图标要重新挂上去。</summary>
    public void Restore()
    {
        if (_disposed)
        {
            return;
        }

        _notify.Visible = false;
        _notify.Visible = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
