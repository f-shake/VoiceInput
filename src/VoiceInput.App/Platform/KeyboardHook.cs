using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceInput.App.Platform;

public readonly record struct KeyEvent(string Name, int VirtualKey, bool IsDown, bool Ctrl, bool Alt);

/// <summary>
/// 低级键盘钩子。按住说话必须拿到“松开”事件，RegisterHotKey 只给按下通知，做不出来。
/// 回调运行在安装钩子的线程上，所以这里自带一个后台线程和它的消息泵：把钩子放在
/// UI 线程上，任何一次阻塞都会连带卡住界面。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;
    private const int KeyDownMask = 0x8000;

    private readonly LowLevelKeyboardProc _callback;
    private readonly Action<string>? _reportFailure;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _handle;
    private uint _threadId;
    private Thread? _thread;
    private Exception? _installError;
    private bool _disposed;

    private int _reportedFailures;

    /// <summary>
    /// 钩子真实收到过按下、还没收到抬起的键。只在钩子线程上读写（按键回调与
    /// <see cref="ModifierKeysDownBesides"/> 的调用方都在这个线程），所以不需要加锁。
    /// </summary>
    private readonly HashSet<int> _heldDown = new();

    public KeyboardHook(Action<string>? reportFailure = null)
    {
        _callback = OnKeyboardMessage;
        _reportFailure = reportFailure;
    }

    /// <summary>所有按键事件；是否与热键相关由调用方判断。</summary>
    public event Action<KeyEvent>? Key;

    public void Install()
    {
        _thread = new Thread(PumpKeyboardMessages) { IsBackground = true, Name = "VoiceInput.KeyboardHook" };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("键盘钩子安装超时。");
        }

        if (_installError is not null)
        {
            throw new InvalidOperationException("键盘钩子安装失败：" + _installError.Message, _installError);
        }

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("键盘钩子安装失败：SetWindowsHookEx 返回空句柄。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    /// <summary>
    /// 低层钩子报的是左右分开的虚拟键码（右 Ctrl 是 0xA3，不是通用的 0x11），所以修饰键
    /// 必须逐个映射：否则日志里会出现 “VK160” 这种看不懂的名字。
    /// </summary>
    public static string NameOf(int virtualKey) => virtualKey switch
    {
        VkRControl => "RightCtrl",
        VkLControl => "LeftCtrl",
        VkLShift => "LeftShift",
        VkRShift => "RightShift",
        VkLMenu => "LeftAlt",
        VkRMenu => "RightAlt",
        VkLWin => "LeftWin",
        VkRWin => "RightWin",
        VkShift => "Shift",
        VkMenu => "Alt",
        VkControl => "Ctrl",
        >= VkF1 and <= VkF12 => "F" + (virtualKey - VkF1 + 1),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        _ => "VK" + virtualKey,
    };

    /// <summary>
    /// 此刻除 <paramref name="virtualKey"/> 之外还按着的**修饰键**，用来判断热键是不是“干净按下”。
    ///
    /// 只认修饰键：真正的组合键都以修饰键打头；字母、数字、小键盘、锁定键按着不该把热键挡掉
    /// （环境里的怪键列不完——本机就有一把恒报按下的虚拟键）。录音期间的取消规则不在此列：
    /// 那时任何真实按键都算数。
    ///
    /// 记账只认钩子真实收到过的按下/抬起，不扫全表：本机实测 <c>GetAsyncKeyState</c> 对某些
    /// 虚拟键会长期报“按下”（小键盘 1 = 0x61 恒为 0x8000），扫全表会把每次按热键都判成组合键。
    /// 反过来，钩子偶尔会漏掉抬起（安全桌面、钩子被系统重装），那些残留用
    /// <c>GetAsyncKeyState</c> 对账清掉——只核对确实进过表的键，假按下的键根本进不来。
    /// </summary>
    public IReadOnlyList<string> ModifierKeysDownBesides(int virtualKey)
    {
        try
        {
            PruneReleasedKeys();

            var held = new List<string>();
            foreach (var vk in _heldDown)
            {
                if (vk != virtualKey && IsModifier(vk))
                {
                    held.Add(NameOf(vk));
                }
            }

            return held;
        }
        catch (Exception ex)
        {
            // 宁可漏判成“干净按下”，也不能让热键从此不响应。
            _reportFailure?.Invoke($"按键判定失败，按干净按下处理：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Ctrl / Shift / Alt / Win 的通用与左右形式：只有它们会让热键变成别人的组合键。</summary>
    private static bool IsModifier(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12                       // Shift / Ctrl / Alt（通用）
        or >= 0xA0 and <= 0xA5                     // 左/右 Shift、Ctrl、Alt
        or 0x5B or 0x5C;                           // 左右 Win

    /// <summary>清掉“见过按下、却从没等到抬起、物理上其实已经松开”的残留键。</summary>
    private void PruneReleasedKeys()
    {
        if (_heldDown.Count == 0)
        {
            return;
        }

        var released = new List<int>();
        foreach (var vk in _heldDown)
        {
            if ((GetAsyncKeyState(vk) & KeyDownMask) == 0)
            {
                released.Add(vk);
            }
        }

        foreach (var vk in released)
        {
            _heldDown.Remove(vk);
        }
    }

    private void PumpKeyboardMessages()
    {
        try
        {
            _threadId = GetCurrentThreadId();

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            _handle = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);

            // 装好就立刻放行 Install；放进 finally 会让它一直等到消息泵退出。
            _ready.Set();
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            // 手工泵消息，不调用 Application.Run()：这是钩子线程，不该在进程里
            // 开第二套 WinForms 消息循环的全局状态，那会干扰界面线程的窗口管理。
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            UnhookWindowsHookEx(_handle);
        }
        catch (Exception ex)
        {
            // 这个线程上的未处理异常会直接杀掉整个进程，必须在这里兜住，
            // 交给 Install 把它变成一条能让用户看懂的错误。
            _installError = ex;
        }
        finally
        {
            _ready.Set();
        }
    }

    private IntPtr OnKeyboardMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var isDown = message is WmKeyDown or WmSysKeyDown;
            var isUp = message is WmKeyUp or WmSysKeyUp;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var ctrl = (GetAsyncKeyState(VkControl) & KeyDownMask) != 0;
                var alt = (GetAsyncKeyState(VkMenu) & KeyDownMask) != 0;

                // 同一颗键在表里还按着又来一次“按下”，那是系统的自动重复：忽略掉。
                // 不忽略的话，“按住 A 再按右 Ctrl”会在半秒后被 A 的重复键取消听写；
                // 长按热键也就不用再靠上层去挡重复事件。
                var repeated = isDown && _heldDown.Contains(data.VirtualKeyCode);

                // 先记账再发事件：订阅者在“按下”里就要问“还按着谁”，这一刻按键自己必须在表里。
                if (isDown)
                {
                    _heldDown.Add(data.VirtualKeyCode);
                }
                else
                {
                    _heldDown.Remove(data.VirtualKeyCode);
                }

                if (repeated)
                {
                    return CallNextHookEx(_handle, code, wParam, lParam);
                }

                try
                {
                    Key?.Invoke(new KeyEvent(
                        NameOf(data.VirtualKeyCode), data.VirtualKeyCode, isDown, ctrl, alt));
                }
                catch (Exception ex)
                {
                    // 回调里抛异常会被系统丢掉，还可能让钩子静默失效，所以必须吞掉；
                    // 但连日志都不留的话，现象会是“热键完全没反应、日志零线索”，
                    // 因此这里记前几次，之后不再刷屏。
                    if (Interlocked.Increment(ref _reportedFailures) <= 3)
                    {
                        _reportFailure?.Invoke($"按键回调异常（第 {_reportedFailures} 次）：{ex}");
                    }
                }
            }
        }

        return CallNextHookEx(_handle, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr wParam, IntPtr lParam);

    // GetModuleHandle 在 kernel32，不在 user32：写错 DLL 会在运行时抛
    // EntryPointNotFoundException，而它发生在钩子线程上，会把整个进程带走。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
