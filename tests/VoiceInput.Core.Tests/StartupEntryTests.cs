using VoiceInput.Core.Config;

namespace VoiceInput.Core.Tests;

public sealed class StartupEntryTests
{
    private const string ExePath = @"C:\Users\tester\AppData\Local\VoiceInput\VoiceInput.exe";

    [Fact]
    public void 路径里有空格时必须加引号()
    {
        var formatted = StartupEntry.Format(@"C:\Program Files\VoiceInput\VoiceInput.exe");

        Assert.StartsWith("\"", formatted, StringComparison.Ordinal);
        Assert.EndsWith("\"", formatted, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\VoiceInput\VoiceInput.exe", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void 格式化后的值能被自己匹配上()
    {
        Assert.True(StartupEntry.Matches(StartupEntry.Format(ExePath), ExePath));
    }

    [Fact]
    public void 手工去掉引号也能匹配()
    {
        Assert.True(StartupEntry.Matches(ExePath, ExePath));
    }

    [Fact]
    public void 大小写不同视为同一条()
    {
        Assert.True(StartupEntry.Matches(ExePath.ToLowerInvariant(), ExePath));
    }

    [Fact]
    public void 程序被移动过就视为没启用()
    {
        Assert.False(StartupEntry.Matches(@"D:\旧目录\VoiceInput.exe", ExePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空值一律视为没启用(string? value)
    {
        Assert.False(StartupEntry.Matches(value, ExePath));
    }

    [Fact]
    public void 空路径格式化会报错()
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupEntry.Format("   "));
    }
}
