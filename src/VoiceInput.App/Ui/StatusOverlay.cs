using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.App.Ui;

/// <summary>
/// 屏幕底部水平居中的小圆：录音中是蓝色麦克风加外圈进度，转写中是三个点，成功变绿打勾，
/// 优化失败变橙画叹号，整体失败变红画叉，并在圆上方挂一句原因。
///
/// 窗口整体鼠标穿透、不抢焦点、不进 Alt+Tab；形状靠 Region 裁出来，所以圆外的透明
/// 角落既看不见也点不到。
/// </summary>
public sealed class StatusOverlay : Form, IStatusView
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    // 逻辑像素，实际绘制时按 DeviceDpi/96 放大。
    private const int BaseDiameter = 31;
    // 圆放大时图标不跟着放大：图标尺寸固定按 24 逻辑像素算，多出来的直径全变成留白。
    private const int BaseIconDiameter = 24;
    private const int BaseGap = 6;
    private const int BaseBottomMargin = 72;
    private const int BaseMaxBubbleWidth = 220;
    private const int AutoHideMilliseconds = 1500;
    private const int TickMilliseconds = 33;

    private static readonly Color RecordingColor = Color.FromArgb(0x2F, 0x6F, 0xEB);
    private static readonly Color TranscribingColor = Color.FromArgb(0x2F, 0x6F, 0xEB);
    private static readonly Color SuccessColor = Color.FromArgb(0x22, 0xA5, 0x5E);
    private static readonly Color FailureColor = Color.FromArgb(0xD9, 0x3A, 0x3A);
    private static readonly Color WarningColor = Color.FromArgb(0xE0, 0x8A, 0x00);
    private static readonly Color BubbleColor = Color.FromArgb(0x1C, 0x1C, 0x1E);

    private static readonly TextFormatFlags BubbleTextFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = TickMilliseconds };
    private readonly System.Windows.Forms.Timer _autoHide = new() { Interval = AutoHideMilliseconds };
    private readonly IDiagnosticsLog? _log;
    private readonly Func<float>? _levelSource;
    private readonly Font _bubbleFont = new("Microsoft YaHei UI", 9f, FontStyle.Regular);

    /// <summary>平滑后的音量，上升即时、下降慢放，免得条子跟着麦克风噪声乱闪。</summary>
    private float _smoothedLevel;

    private OverlayState _state = OverlayState.Hidden;
    private string _bubbleText = string.Empty;
    private DateTime _stateSinceUtc = DateTime.UtcNow;
    private Region? _region;
    private int _maxSeconds = 118;

    public StatusOverlay(IDiagnosticsLog? log = null, AppConfig? config = null, Func<float>? level = null)
    {
        _log = log;
        _levelSource = level;
        _maxSeconds = Math.Max(1, config?.MaxSeconds ?? 118);
        Icon = AppIcon.Shared;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = BubbleColor;
        Size = new Size(BaseDiameter, BaseDiameter);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _tick.Tick += (_, _) => Invalidate();
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            HideOverlay();
        };
    }

    /// <summary>资源管理器重启后托盘图标要重挂，这个消息只有窗口收得到。</summary>
    public event Action? TaskbarCreated;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExTransparent | WsExToolWindow;
            return parameters;
        }
    }

    void IStatusView.Show(OverlayState state, string text)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => Apply(state, text)));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        Apply(state, text);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryEnableRoundedCorners();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TaskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke();
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var diameter = (int)(BaseDiameter * scale);

        // 圆心必须落在窗口正中：窗口按屏幕中线定位，圆放在 x=0 会整体偏左半个气泡宽，
        // 有气泡时还会把气泡挤到窗口外、被裁成换行。
        var circle = new Rectangle((Width - diameter) / 2, Height - diameter, diameter, diameter);
        var center = new Point(circle.Left + diameter / 2, circle.Top + diameter / 2);

        if (!string.IsNullOrEmpty(_bubbleText))
        {
            var bubble = new Rectangle(
                0,
                0,
                Width,
                Height - diameter - (int)(BaseGap * scale));
            DrawBubble(graphics, bubble, scale);
        }

        using (var fill = new SolidBrush(StateColor()))
        {
            graphics.FillEllipse(fill, circle);
        }

        var ringWidth = Math.Max(1.5f, 1.5f * scale);
        var iconDiameter = Math.Max(8, (int)(BaseIconDiameter * scale));
        switch (_state)
        {
            case OverlayState.Recording:
                DrawProgressRing(graphics, circle, ringWidth);
                DrawWaveform(graphics, center, iconDiameter, scale, CurrentLevel());
                break;
            case OverlayState.Transcribing:
                DrawDots(graphics, center, iconDiameter, scale);
                break;
            case OverlayState.Success:
                DrawCheck(graphics, center, iconDiameter, scale);
                break;
            case OverlayState.Warning:
                DrawExclamation(graphics, center, iconDiameter, scale);
                break;
            case OverlayState.Failure:
                DrawCross(graphics, center, iconDiameter, scale);
                break;
        }
    }

    /// <summary>
    /// 取一次麦克风音量并平滑。上升即时是为了“一开口就有反应”，下降放慢是因为
    /// 真实音量本来就抖，照搬会闪成噪点。
    /// </summary>
    private float CurrentLevel()
    {
        var target = Math.Clamp(_levelSource?.Invoke() ?? 0, 0, 1);
        _smoothedLevel = target > _smoothedLevel
            ? target
            : _smoothedLevel + ((target - _smoothedLevel) * 0.15f);
        return _smoothedLevel;
    }

    private Color StateColor() => _state switch
    {
        OverlayState.Success => SuccessColor,
        OverlayState.Warning => WarningColor,
        OverlayState.Failure => FailureColor,
        OverlayState.Transcribing => TranscribingColor,
        _ => RecordingColor,
    };

    private void DrawBubble(Graphics graphics, Rectangle bounds, float scale)
    {
        var radius = (int)(6 * scale);
        using var path = RoundedRect(bounds, radius);
        using var fill = new SolidBrush(BubbleColor);
        graphics.FillPath(fill, path);

        // 测量与绘制都用 TextRenderer，并且 SingleLine：用 Graphics.DrawString 时
        // 它会自己算一遍排版，量出来的宽度对不上就换行，第二行随即被窗口高度裁掉。
        var inset = new Rectangle(
            bounds.Left + (int)(8 * scale),
            bounds.Top,
            bounds.Width - (int)(16 * scale),
            bounds.Height);
        TextRenderer.DrawText(
            graphics,
            _bubbleText,
            _bubbleFont,
            inset,
            Color.White,
            BubbleTextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var size = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
        path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
        path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>进度环画在圆内边缘：画在外面会被 FillEllipse 反过来盖住。</summary>
    private void DrawProgressRing(Graphics graphics, Rectangle circle, float width)
    {
        var elapsed = (DateTime.UtcNow - _stateSinceUtc).TotalSeconds;
        var ratio = Math.Clamp(elapsed / _maxSeconds, 0, 1);
        if (ratio <= 0)
        {
            return;
        }

        var inset = (int)Math.Ceiling(width);
        var ring = Rectangle.Inflate(circle, -inset, -inset);
        using var pen = new Pen(Color.FromArgb(230, Color.White), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(pen, ring, -90, (float)(360 * ratio));
    }

    /// <summary>
    /// 录音中用四根高低不一的竖条（波形），24 像素下也一眼认得出是“在听”。
    /// 条高由麦克风实际音量决定：安静时压得很矮，说话越大越高。
    /// </summary>
    private static void DrawWaveform(Graphics graphics, Point center, int diameter, float scale, float level)
    {
        var barWidth = Math.Max(2, (int)(2.4f * scale));
        var gap = Math.Max(2, (int)(2.6f * scale));
        var factors = new[] { 0.34f, 0.66f, 0.46f, 0.78f };

        var totalWidth = (barWidth * factors.Length) + (gap * (factors.Length - 1));
        var left = center.X - (totalWidth / 2);

        // 0.15 是静音时的底：条子还在，证明在录；1.0 是满格。
        var gain = 0.15f + (0.85f * Math.Clamp(level, 0, 1));
        using var brush = new SolidBrush(Color.White);
        foreach (var factor in factors)
        {
            var height = Math.Max(barWidth, (int)(diameter * 0.62f * factor * gain));
            var bar = new Rectangle(left, center.Y - (height / 2), barWidth, height);
            using var path = RoundedRect(bar, barWidth / 2);
            graphics.FillPath(brush, path);
            left += barWidth + gap;
        }
    }

    /// <summary>橙色警告用叹号：和红色的叉一眼能分开。</summary>
    private static void DrawExclamation(Graphics graphics, Point center, int diameter, float scale)
    {
        var width = Math.Max(2f, 1.8f * scale);
        using var pen = new Pen(Color.White, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        var top = center.Y - (diameter * 0.20f);
        var bottom = center.Y + (diameter * 0.02f);
        graphics.DrawLine(pen, center.X, top, center.X, bottom);

        var radius = Math.Max(1.2f, width * 0.62f);
        using var brush = new SolidBrush(Color.White);
        graphics.FillEllipse(brush, center.X - radius, center.Y + (diameter * 0.12f), radius * 2, radius * 2);
    }

    /// <summary>失败用叉号：感叹号在这个尺寸下容易和警告混淆，所以两者形状必须不同。</summary>
    private static void DrawCross(Graphics graphics, Point center, int diameter, float scale)
    {
        var width = Math.Max(2f, 1.7f * scale);
        using var pen = new Pen(Color.White, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // 0.145 是看着最接近“小叉”的比例：再大就顶到圆边，显得像禁止符。
        var size = diameter * 0.145f;
        graphics.DrawLine(
            pen,
            center.X - size,
            center.Y - size,
            center.X + size,
            center.Y + size);
        graphics.DrawLine(
            pen,
            center.X - size,
            center.Y + size,
            center.X + size,
            center.Y - size);
    }

    private static void DrawDots(Graphics graphics, Point center, int diameter, float scale)
    {
        var radius = Math.Max(1f, 1.4f * scale);
        var spacing = diameter * 0.18f;
        using var white = new SolidBrush(Color.White);
        for (var index = -1; index <= 1; index++)
        {
            graphics.FillEllipse(
                white,
                center.X + index * spacing - radius,
                center.Y - radius,
                radius * 2,
                radius * 2);
        }
    }

    private static void DrawCheck(Graphics graphics, Point center, int diameter, float scale)    {
        var width = Math.Max(1.5f, 1.8f * scale);
        using var pen = new Pen(Color.White, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var size = diameter * 0.26f;
        graphics.DrawLines(
            pen,
            new[]
            {
                new PointF(center.X - size, center.Y),
                new PointF(center.X - size * 0.15f, center.Y + size * 0.7f),
                new PointF(center.X + size, center.Y - size * 0.6f),
            });
    }


    private void Apply(OverlayState state, string text)
    {
        _tick.Stop();
        _autoHide.Stop();

        if (state == OverlayState.Hidden)
        {
            HideOverlay();
            return;
        }

        var previous = _state;
        _state = state;
        if (previous != state)
        {
            _stateSinceUtc = DateTime.UtcNow;
        }

        // 录音中不挂气泡（会挡住正在输入的那行，波形本身就够看）；其余状态都带一句文案：
        // 截断提示、“已粘贴 N 字（已优化/优化失败）”都在这条路径上，只变颜色会让用户看不到字数。
        _bubbleText = state == OverlayState.Recording ? string.Empty : text;

        LayoutWindow();
        if (!Visible)
        {
            Visible = true;
        }

        ForceShow();

        _log?.Info(
            $"状态条 {state}：文字 {text}，WinForms 可见 {Visible}，"
            + $"Win32 可见 {IsWindowVisible(Handle)}，位置 {Location.X},{Location.Y}，"
            + $"尺寸 {Width}x{Height}，DPI {DeviceDpi}");

        if (state == OverlayState.Recording)
        {
            _tick.Start();
        }
        else
        {
            _smoothedLevel = 0;
            if (state is OverlayState.Success or OverlayState.Warning or OverlayState.Failure)
            {
                _autoHide.Start();
            }
        }
    }

    /// <summary>
    /// 水平居中、离工作区底部固定距离。工作区已经扣掉任务栏，所以不必再猜任务栏高度。
    /// </summary>
    private void LayoutWindow()
    {
        var scale = DeviceDpi / 96f;
        var diameter = (int)(BaseDiameter * scale);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);

        var bubbleWidth = 0;
        var bubbleHeight = 0;
        if (!string.IsNullOrEmpty(_bubbleText))
        {
            using var graphics = CreateGraphics();
            var measured = graphics.MeasureString(_bubbleText, _bubbleFont);
            bubbleWidth = Math.Min((int)measured.Width + (int)(16 * scale), (int)(BaseMaxBubbleWidth * scale));
            bubbleHeight = (int)measured.Height + (int)(10 * scale);
        }

        var width = Math.Max(diameter, bubbleWidth);
        var height = diameter + (bubbleHeight > 0 ? bubbleHeight + (int)(BaseGap * scale) : 0);
        Size = new Size(width, height);

        var left = area.Left + (area.Width - width) / 2;
        var top = area.Bottom - (int)(BaseBottomMargin * scale) - height;
        Location = new Point(left, Math.Max(area.Top, top));

        ApplyHitRegion(diameter, bubbleHeight, scale);
    }

    /// <summary>圆外的透明区域不参与命中测试，避免挡住下方的点击（虽然整窗已是鼠标穿透）。</summary>
    private void ApplyHitRegion(int diameter, int bubbleHeight, float scale)
    {
        var region = new Region();
        region.MakeEmpty();

        var circle = new Rectangle((Width - diameter) / 2, Height - diameter, diameter, diameter);
        using var circlePath = new GraphicsPath();
        circlePath.AddEllipse(circle);
        region.Union(circlePath);

        if (bubbleHeight > 0)
        {
            using var bubblePath = RoundedRect(
                new Rectangle(0, 0, Width, bubbleHeight), (int)(6 * scale));
            region.Union(bubblePath);
        }

        _region?.Dispose();
        _region = region;
        Region = region;
    }

    private void HideOverlay()
    {
        _tick.Stop();
        _autoHide.Stop();
        Visible = false;
        Region = null;
        _region?.Dispose();
        _region = null;
    }

    /// <summary>
    /// 只把 Visible 置真不够：WinForms 的可见标志是它自己的记录，不等于窗口已经上屏。
    /// 而且这里绕过了 WinForms 的显示路径，TopMost / Opacity 这两个属性根本不会作用到
    /// 窗口句柄上（实测 ExStyle 里既没有 TOPMOST 也没有 LAYERED），所以样式全部自己设。
    /// </summary>
    private void ForceShow()
    {
        var style = GetWindowLong(Handle, GwlExStyle).ToInt64();
        var wanted = style | WsExNoActivate | WsExTransparent | WsExToolWindow | WsExLayered;
        if (wanted != style)
        {
            SetWindowLong(Handle, GwlExStyle, new IntPtr(wanted));
        }

        // 不用 Form.Opacity：那条路要 WinForms 自己去建分层窗口，我们已经绕过它了。
        SetLayeredWindowAttributes(Handle, 0, (byte)(255 * 0.94), LwaAlpha);

        ShowWindow(Handle, SwShowNoActivate);
        SetWindowPos(
            Handle,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void TryEnableRoundedCorners()
    {
        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const int SwShowNoActivate = 4;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int WsExLayered = 0x00080000;
    private const int GwlExStyle = -20;
    private const uint LwaAlpha = 0x00000002;
    private static readonly IntPtr HwndTopMost = new(-1);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _autoHide.Dispose();
            _bubbleFont.Dispose();
            _region?.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLong(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
