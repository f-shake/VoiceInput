using VoiceInput.Core.Diagnostics;

namespace VoiceInput.Core.Tests;

public sealed class RotatingFileLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "voiceinput-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void 写入后文件里能找到这一行()
    {
        using (var log = new RotatingFileLog(_directory))
        {
            log.Info("就绪");
        }

        var text = File.ReadAllText(Path.Combine(_directory, "voiceinput.log"));
        Assert.Contains("[INFO] 就绪", text);
    }

    [Fact]
    public void 超过上限时轮转出归档文件()
    {
        using (var log = new RotatingFileLog(_directory, "app.log", maxBytes: 200, keepFiles: 2))
        {
            for (var index = 0; index < 40; index++)
            {
                log.Info("第 " + index + " 行，用来把文件撑到超过上限。");
            }
        }

        Assert.True(File.Exists(Path.Combine(_directory, "app.log")));
        Assert.True(File.Exists(Path.Combine(_directory, "app.1.log")));
    }

    [Fact]
    public void 目录不可写时静默降级()
    {
        var filePath = Path.Combine(_directory, "这是文件不是目录");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(filePath, "占位");

        using var log = new RotatingFileLog(filePath);

        Assert.False(log.IsEnabled);
        log.Error("这条写不进去，但不许抛异常");
    }

    [Fact]
    public void 多行消息被压成一行()
    {
        using (var log = new RotatingFileLog(_directory))
        {
            log.Warn("第一行\n第二行");
        }

        var lines = File.ReadAllLines(Path.Combine(_directory, "voiceinput.log"));
        Assert.Single(lines);
        Assert.Contains("第一行 第二行", lines[0]);
    }
}
