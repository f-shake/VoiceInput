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
    private const int VkRControl = 0xA3;
    private const int VkLControl = 0xA2;
    private const int VkShift = 0x10;
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;

    private readonly LowLevelKeyboardProc _callback;
    private readonly Action<string>? _reportFailure;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _handle;
    private uint _threadId;
    private Thread? _thread;
    private Exception? _installError;
    private bool _disposed;

    private int _reportedFailures;

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

    public static string NameOf(int virtualKey) => virtualKey switch
    {
        VkRControl => "RightCtrl",
        VkLControl => "LeftCtrl",
        VkShift => "Shift",
        VkMenu => "Alt",
        VkControl => "Ctrl",
        >= VkF1 and <= VkF12 => "F" + (virtualKey - VkF1 + 1),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        _ => "VK" + virtualKey,
    };

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
                var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                var alt = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;

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
