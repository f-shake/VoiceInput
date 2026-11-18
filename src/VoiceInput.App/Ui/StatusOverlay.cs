using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.App.Ui;

/// <summary>
/// 状态小圆：录音中是蓝色麦克风加外圈进度，转写中是三个点，成功变绿打勾，
/// 优化失败变橙画叹号，整体失败变红画叉，并在圆旁挂一句原因。
///
/// 圆心位置存成主屏工作区的百分比，可以按住圆或气泡拖到别处。窗口不抢焦点、不进 Alt+Tab；
/// 形状靠 Region 裁出来，Region 之外的透明区域不接鼠标，所以只有圆和气泡本身能拖，
/// 其余地方照旧点得穿——整窗不能再挂 WS_EX_TRANSPARENT，那会把圆也一起穿透。
/// </summary>
public sealed class StatusOverlay : Form, IStatusView
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    // 逻辑像素，实际绘制时按 DeviceDpi/96 放大。
    private const int BaseDiameter = 31;
    // 圆放大时图标不跟着放大：图标尺寸固定按 24 逻辑像素算，多出来的直径全变成留白。
    private const int BaseIconDiameter = 24;
    private const int BaseGap = 6;
    private const int BaseMaxBubbleWidth = 220;
    private const int AutoHideMilliseconds = 1500;
    private const int TickMilliseconds = 33;

    /// <summary>
    /// 条高上限相对图标直径的比例。当前增益下最高只到 0.78，这里留 0.88 是防以后把
    /// 增益曲线或比例再调大时顶出圆。改造前正常说话约 7 像素，现在约 14 像素。
    /// </summary>
    private const float WaveformHeightCap = 0.88f;

    /// <summary>
    /// 气泡离工作区顶边不足这个逻辑像素数时就翻到圆下方。取 12（约一行字高）而不是更小：
    /// 锚点下限 0.05 在 200% 缩放下正好让气泡贴到离顶边 8 像素，4 像素的余量卡在边界上，
    /// 翻转永远触发不了，等于白写。
    /// </summary>
    private const float BubbleEdgeMargin = 12f;

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

    /// <summary>圆心在主屏工作区里的百分比。拖动只改这两个值，窗口位置由它们推出来。</summary>
    private double _anchorX = AppConfig.DefaultOverlayX;
    private double _anchorY = AppConfig.DefaultOverlayY;

    /// <summary>按下点相对圆心的偏移：拖动时让圆心跟着鼠标走，圆不会跳到光标底下。</summary>
    private Point _dragOffset;
    private bool _dragging;

    /// <summary>气泡高度（像素，0 表示这一帧没有气泡）与它摆在圆的上方还是下方。</summary>
    private int _bubbleHeight;
    private bool _bubbleBelow;

    /// <summary>
    /// 两个形状在窗口里的位置。窗口是“圆 + 气泡”的包围盒，圆心由锚点定死、气泡偏心时
    /// 自己贴边横移，所以状态切换（有无气泡）时圆不会跟着窗口漂。
    /// </summary>
    private Rectangle _circleRect;
    private Rectangle _bubbleRect;

    public StatusOverlay(IDiagnosticsLog? log = null, AppConfig? config = null, Func<float>? level = null)
    {
        _log = log;
        _levelSource = level;
        _maxSeconds = Math.Max(1, config?.MaxSeconds ?? 118);
        _anchorX = Math.Clamp(
            config?.OverlayX ?? AppConfig.DefaultOverlayX,
            AppConfig.OverlayMinFraction,
            AppConfig.OverlayMaxFraction);
        _anchorY = Math.Clamp(
            config?.OverlayY ?? AppConfig.DefaultOverlayY,
            AppConfig.OverlayMinFraction,
            AppConfig.OverlayMaxFraction);
        // 光标形状是唯一不占地方的“这里能拖”提示。
        Cursor = Cursors.SizeAll;
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

    /// <summary>拖动结束，参数是新的圆心百分比。写配置文件由装配层负责，这里不碰磁盘。</summary>
    public event Action<double, double>? Moved;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var center = CircleCenterScreen();
        _dragOffset = new Point(Cursor.Position.X - center.X, Cursor.Position.Y - center.Y);
        _dragging = true;
        Capture = true;

        // 成功/失败的圆只停 1.5 秒：拖到一半被自己藏起来太怪了。
        _autoHide.Stop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var area = WorkingArea();
        var center = new Point(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
        _anchorX = Math.Clamp(
            (double)(center.X - area.Left) / Math.Max(1, area.Width),
            AppConfig.OverlayMinFraction,
            AppConfig.OverlayMaxFraction);
        _anchorY = Math.Clamp(
            (double)(center.Y - area.Top) / Math.Max(1, area.Height),
            AppConfig.OverlayMinFraction,
            AppConfig.OverlayMaxFraction);

        // 边拖边重新布局：贴到顶边时气泡要当场翻到下面，等松手才翻会看着像跳了一下。
        LayoutWindow();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndDrag(commit: true);
    }

    /// <summary>
    /// 鼠标捕获被系统收回（Alt+Tab、窗口被隐藏、另一个窗口抢走鼠标）时 MouseUp 不会再来：
    /// 这里按“拖完了”收尾，否则 _dragging 永远是 true，下一次显示时鼠标一动窗口就跳到光标下。
    /// </summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture)
        {
            EndDrag(commit: true);
        }
    }

    /// <summary>收尾一次拖动：复位状态、放掉捕获、恢复自动回落，并把新位置报给装配层。</summary>
    private void EndDrag(bool commit)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false;

        if (Visible && _state is OverlayState.Success or OverlayState.Warning or OverlayState.Failure)
        {
            _autoHide.Start();
        }

        if (commit)
        {
            Moved?.Invoke(_anchorX, _anchorY);
        }
    }

    private Point CircleCenterScreen() => new(
        Location.X + _circleRect.Left + (_circleRect.Width / 2),
        Location.Y + _circleRect.Top + (_circleRect.Height / 2));

    private static Rectangle WorkingArea() =>
        Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        var graphics = e.Graphics;

        // 先整块清底：窗口是分层窗口 + 自定义区域，尺寸和位置每换一个状态都会变，
        // 不清底时上一帧的圆和字会留在重叠区域里（实测出现过蓝色圆压在新气泡上）。
        graphics.Clear(BubbleColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var diameter = _circleRect.Width;
        var circle = _circleRect;
        var center = new Point(circle.Left + (diameter / 2), circle.Top + (diameter / 2));

        if (!_bubbleRect.IsEmpty)
        {
            DrawBubble(graphics, _bubbleRect, scale);
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

        // 增益曲线在 AudioLevel 里（可单测）：静音 0.15 的底，满格 1.0。
        var gain = AudioLevel.ToDisplayGain(level);
        var maxHeight = Math.Max(barWidth, (int)(diameter * WaveformHeightCap));
        using var brush = new SolidBrush(Color.White);
        foreach (var factor in factors)
        {
            var height = (int)(diameter * factor * gain);
            height = Math.Clamp(height, barWidth, maxHeight);
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

        // 换状态会同时改尺寸、位置与 Region，必须整窗重画一遍；否则残留的旧像素会
        // 压在新画的内容上。
        Invalidate(true);
        Update();

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

            // 拖动期间不挂自动回落：否则拖到一半窗口自己消失，鼠标捕获被收回，这次拖动就丢了。
            if (!_dragging && _state is OverlayState.Success or OverlayState.Warning or OverlayState.Failure)
            {
                _autoHide.Start();
            }
        }
    }

    /// <summary>
    /// 按百分比把圆心放到主屏工作区里（工作区已扣任务栏），窗口则取“圆 + 气泡”的包围盒：
    /// 圆心由锚点定死，气泡偏心时自己横移贴边，上边放不下就翻到圆下方。这样“录音中（无气泡）”
    /// 与“转写中（有气泡）”之间圆心一分都不会动。
    /// </summary>
    private void LayoutWindow()
    {
        var scale = DeviceDpi / 96f;
        var diameter = (int)(BaseDiameter * scale);
        var gap = (int)(BaseGap * scale);
        var area = WorkingArea();

        _bubbleHeight = 0;
        var bubbleWidth = 0;
        if (!string.IsNullOrEmpty(_bubbleText))
        {
            using var graphics = CreateGraphics();
            var measured = graphics.MeasureString(_bubbleText, _bubbleFont);
            bubbleWidth = Math.Min(
                (int)measured.Width + (int)(16 * scale),
                Math.Min((int)(BaseMaxBubbleWidth * scale), area.Width));
            _bubbleHeight = Math.Min((int)measured.Height + (int)(10 * scale), area.Height);
        }

        var radius = diameter / 2;
        var centerX = Math.Clamp(
            area.Left + (int)Math.Round(area.Width * _anchorX),
            area.Left + radius,
            Math.Max(area.Left + radius, area.Right - radius));
        var centerY = Math.Clamp(
            area.Top + (int)Math.Round(area.Height * _anchorY),
            area.Top + radius,
            Math.Max(area.Top + radius, area.Bottom - radius));

        var circle = new Rectangle(centerX - radius, centerY - radius, diameter, diameter);

        var bubble = Rectangle.Empty;
        _bubbleBelow = false;
        if (_bubbleHeight > 0)
        {
            // 上方放不下（含贴边余量）就翻到圆下方：拖到贴顶时气泡自动钻到圆的下面去。
            _bubbleBelow = circle.Top - gap - _bubbleHeight < area.Top + (int)(BubbleEdgeMargin * scale);
            var bubbleTop = _bubbleBelow ? circle.Bottom + gap : circle.Top - gap - _bubbleHeight;
            bubbleTop = Math.Clamp(bubbleTop, area.Top, Math.Max(area.Top, area.Bottom - _bubbleHeight));
            var bubbleLeft = Math.Clamp(
                centerX - (bubbleWidth / 2),
                area.Left,
                Math.Max(area.Left, area.Right - bubbleWidth));
            bubble = new Rectangle(bubbleLeft, bubbleTop, bubbleWidth, _bubbleHeight);
        }

        // 窗口只是两个形状的包围盒：所以不把 Location 钳进工作区——那会把圆心一起推走，
        // 宽气泡贴边时正是这样让圆心偏出十几个像素的。
        var bounds = bubble.IsEmpty ? circle : Rectangle.Union(circle, bubble);
        Size = new Size(bounds.Width, bounds.Height);
        Location = bounds.Location;

        _circleRect = new Rectangle(circle.X - bounds.X, circle.Y - bounds.Y, diameter, diameter);
        _bubbleRect = bubble.IsEmpty
            ? Rectangle.Empty
            : new Rectangle(bubble.X - bounds.X, bubble.Y - bounds.Y, bubble.Width, bubble.Height);

        ApplyHitRegion(scale);

        // 拖动时每帧都在改尺寸、位置与 Region，这里同步重画一次，免得留下上一帧的残影。
        Invalidate(true);
        Update();

        // 圆心是唯一的位置状态：窗口怎么长都该让圆心落在锚点上。对不上就是布局算错了，
        // 这种偏差肉眼只能看出“轻微”，所以让它每次都被记下来。
        if (!_dragging)
        {
            var actual = CircleCenterScreen();
            if (actual.X != centerX || actual.Y != centerY)
            {
                _log?.Warn(
                    $"状态条布局偏离：圆心期望 {centerX},{centerY}，实际 {actual.X},{actual.Y}，"
                    + $"窗口 {Width}x{Height} @ {Location.X},{Location.Y}，气泡高 {_bubbleHeight}，"
                    + $"气泡在下方 {_bubbleBelow}，工作区 {area}");
            }
        }
    }

    /// <summary>Region 之外不接鼠标，所以圆和气泡之外照旧点得穿；拖动也只在这两个形状上生效。</summary>
    private void ApplyHitRegion(float scale)
    {
        var region = new Region();
        region.MakeEmpty();

        using (var circlePath = new GraphicsPath())
        {
            circlePath.AddEllipse(_circleRect);
            region.Union(circlePath);
        }

        if (!_bubbleRect.IsEmpty)
        {
            using var bubblePath = RoundedRect(_bubbleRect, (int)(6 * scale));
            region.Union(bubblePath);
        }

        _region?.Dispose();
        _region = region;
        Region = region;
    }

    private void HideOverlay()
    {
        // 拖动中被藏起来（自动回落）时也得把这次拖动收尾，否则新位置既没落盘，
        // 下一次显示还会被残留的拖动状态把窗口拽到光标底下。
        EndDrag(commit: true);

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
        var wanted = style | WsExNoActivate | WsExToolWindow | WsExLayered;
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
