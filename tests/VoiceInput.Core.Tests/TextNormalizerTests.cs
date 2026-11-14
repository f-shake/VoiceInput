using VoiceInput.Core.Text;

namespace VoiceInput.Core.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  你好  ", "你好")]
    public void 空输入与首尾空白(string? raw, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(raw));
    }

    [Fact]
    public void 换行被压成单个空格()
    {
        Assert.Equal("第一行 第二行", TextNormalizer.Normalize("第一行\n第二行"));
    }

    [Fact]
    public void 连续换行与回车不留下空行()
    {
        Assert.Equal("甲 乙 丙", TextNormalizer.Normalize("甲\r\n\r\n乙\r丙"));
    }

    [Fact]
    public void 制表符与全角空格折叠成一个空格()
    {
        Assert.Equal("甲 乙 丙 丁", TextNormalizer.Normalize("甲\t乙\u3000丙  丁"));
    }

    [Fact]
    public void 控制字符被删除()
    {
        Assert.Equal("你好", TextNormalizer.Normalize("你\u0007好\u0000"));
    }

    [Fact]
    public void 五千字长文本不被截断()
    {
        var raw = new string('词', 5000);

        Assert.Equal(5000, TextNormalizer.Normalize(raw).Length);
    }

    [Fact]
    public void 表情符号原样保留()
    {
        Assert.Equal("好耶🎉", TextNormalizer.Normalize("好耶🎉"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\n\t", true)]
    [InlineData("a", false)]
    [InlineData("。", false)]
    public void 判定空白文本(string text, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.IsBlank(text));
    }

    [Fact]
    public void OneLine把换行压平并截断()
    {
        Assert.Equal("甲 乙 丙", TextNormalizer.OneLine("甲\n乙\t丙"));

        var clamped = TextNormalizer.OneLine(new string('到', 10), limit: 5);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
        Assert.Equal(5, clamped.Length);
    }
}
