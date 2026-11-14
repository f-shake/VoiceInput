using System.Globalization;
using System.Text;

namespace VoiceInput.Core.Diagnostics;

/// <summary>
/// 按大小轮转的文本日志。写不进去就静默降级成不写：日志不该成为听写失败的
/// 原因，尤其是在磁盘满或目录权限被改之后。
/// </summary>
public sealed class RotatingFileLog : IDiagnosticsLog, IDisposable
{
    public const long DefaultMaxBytes = 2 * 1024 * 1024;
    public const int DefaultKeepFiles = 3;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _fileName;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private StreamWriter? _writer;
    private long _written;
    private bool _disabled;
    private bool _disposed;

    public RotatingFileLog(
        string directory,
        string fileName = "voiceinput.log",
        long maxBytes = DefaultMaxBytes,
        int keepFiles = DefaultKeepFiles)
    {
        _directory = directory;
        _fileName = fileName;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
        TryOpen();
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return !_disabled && _writer is not null;
            }
        }
    }

    public string CurrentPath => Path.Combine(_directory, _fileName);

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}",
            DateTime.Now,
            level,
            message.ReplaceLineEndings(" "));

        lock (_gate)
        {
            if (_disabled || _writer is null)
            {
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_written + bytes > _maxBytes)
                {
                    Rotate();

                    // 轮转后新文件可能开不出来（磁盘满、被杀软占着），
                    // 这时已经没有 writer，继续写就是 NullReferenceException。
                    if (_writer is null)
                    {
                        return;
                    }
                }

                _writer.WriteLine(line);
                _writer.Flush();
                _written += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 写不进去就关掉自己，别让每次听写都去碰一次坏路径。
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        for (var index = _keepFiles - 1; index >= 0; index--)
        {
            var source = ArchivePath(index);
            if (!File.Exists(source))
            {
                continue;
            }

            var target = ArchivePath(index + 1);
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(source, target);
        }

        _written = 0;
        TryOpen(truncate: true);
    }

    private string ArchivePath(int index) =>
        Path.Combine(_directory, index == 0 ? _fileName : Path.GetFileNameWithoutExtension(_fileName) + "." + index + ".log");

    private void TryOpen(bool truncate = false)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var stream = new FileStream(
                CurrentPath,
                truncate ? FileMode.Create : FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            _written = truncate ? 0 : stream.Length;
            _disabled = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _writer = null;
            _disabled = true;
        }
    }
}
