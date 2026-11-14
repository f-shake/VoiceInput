using System.Runtime.InteropServices;
using System.Text;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.App.Platform;

/// <summary>
/// 备份剪贴板 → 写入文本 → 发 Ctrl+V → 还原。
///
/// 写入走原始 Win32 剪贴板 API 而不是 WinForms 的 <c>Clipboard.SetDataObject</c>：
/// 后者内部是 OLE，失败时只给一个笼统的 ExternalException，重试也由它自己决定；
/// 原始 API 能自己控制 OpenClipboard 的重试节奏，失败时拿得到 Win32 错误码。
/// 备份与还原仍用 OLE，因为要保留图片。
/// </summary>
public sealed class ClipboardPaster : IPaster
{
    private const int ClipboardAttempts = 20;
    private const int ClipboardRetryDelayMs = 50;
    private const int RestoreAttempts = 3;
    private const int RestoreDelayMs = 150;
    private const int VkControl = 0x11;
    private const int VkV = 0x56;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private readonly AppConfig _config;
    private readonly Control _uiAnchor;
    private int _lastWin32Error;

    public ClipboardPaster(AppConfig config, Control uiAnchor)
    {
        _config = config;
        _uiAnchor = uiAnchor;
    }

    public PasteResult Paste(string text)
    {
        // 备份与还原用的是 OLE 接口，只能在 STA 线程碰，所以整体挪到 UI 线程执行。
        if (_uiAnchor.IsDisposed)
        {
            throw new VoiceInputException(ErrorCodes.PasteFailed, ErrorCatalog.PasteFailed);
        }

        if (_uiAnchor.InvokeRequired)
        {
            return (PasteResult)_uiAnchor.Invoke(new Func<PasteResult>(() => Paste(text)));
        }

        return PasteOnUiThread(text);
    }

    private PasteResult PasteOnUiThread(string text)
    {
        var backup = _config.RestoreClipboard ? CloneClipboard() : null;

        if (!TryWriteText(text))
        {
            throw new VoiceInputException(
                ErrorCodes.ClipboardWriteFailed,
                ErrorCatalog.ClipboardWriteFailed,
                detail: $"Win32 错误码 {_lastWin32Error}，连续 {ClipboardAttempts} 次尝试失败，"
                    + $"线程单元 {Thread.CurrentThread.GetApartmentState()}");
        }

        if (_config.PasteDelayMilliseconds > 0)
        {
            Thread.Sleep(_config.PasteDelayMilliseconds);
        }

        if (!SendCtrlV())
        {
            // 这条路径不还原：粘贴失败时剪贴板里那份文本是用户唯一的补救手段。
            throw new VoiceInputException(ErrorCodes.PasteFailed, ErrorCatalog.PasteFailed);
        }

        Thread.Sleep(RestoreDelayMs);

        var restored = backup is not null && TryRestore(backup);
        return new PasteResult(Pasted: true, Restored: restored, HadBackup: backup is not null);
    }

    /// <summary>
    /// 只备份文本与图片：这两样覆盖了实际会丢失的内容，而且都能可靠还原。
    /// 文件列表、RTF、HTML 之类还原起来代价与风险都更高，宁可不碰。
    /// </summary>
    private static DataObject? CloneClipboard()
    {
        try
        {
            var clone = new DataObject();
            var hasContent = false;

            var text = Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                clone.SetText(text);
                hasContent = true;
            }

            var image = Clipboard.GetImage();
            if (image is not null)
            {
                clone.SetImage(image);
                hasContent = true;
            }

            return hasContent ? clone : null;
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or OutOfMemoryException)
        {
            // 备份失败不影响这次听写，只是还原会少一步。
            return null;
        }
    }

    private static bool TryRestore(DataObject backup)
    {
        for (var attempt = 0; attempt < RestoreAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(backup, copy: true);
                return true;
            }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return false;
    }

    private bool TryWriteText(string text)
    {
        var payload = Encoding.Unicode.GetBytes(text + "\0");

        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            if (!OpenClipboard(_uiAnchor.Handle))
            {
                _lastWin32Error = Marshal.GetLastWin32Error();
                Thread.Sleep(ClipboardRetryDelayMs);
                continue;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    _lastWin32Error = Marshal.GetLastWin32Error();
                    continue;
                }

                var handle = GlobalAlloc(GmemMoveable, (UIntPtr)payload.Length);
                if (handle == IntPtr.Zero)
                {
                    _lastWin32Error = Marshal.GetLastWin32Error();
                    continue;
                }

                var target = GlobalLock(handle);
                if (target == IntPtr.Zero)
                {
                    _lastWin32Error = Marshal.GetLastWin32Error();
                    GlobalFree(handle);
                    continue;
                }

                try
                {
                    Marshal.Copy(payload, 0, target, payload.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
                {
                    _lastWin32Error = Marshal.GetLastWin32Error();
                    GlobalFree(handle);
                    continue;
                }

                // 成功时内存所有权归系统，不能再自己释放。
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, keyUp: false),
            KeyboardInput(VkV, keyUp: false),
            KeyboardInput(VkV, keyUp: true),
            KeyboardInput(VkControl, keyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return sent == inputs.Length;
    }

    private static Input KeyboardInput(int virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = (ushort)virtualKey,
                Scan = 0,
                Flags = keyUp ? KeyeventfKeyup : 0,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // 必须留出 MOUSEINPUT 的尺寸：SendInput 校验的是结构体大小，
    // 只声明键盘成员会让 sizeof(Input) 对不上，调用直接失败。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
