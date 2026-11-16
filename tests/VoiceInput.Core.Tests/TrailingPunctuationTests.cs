using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Tests;

public sealed class TrailingPunctuationTests
{
    [Theory]
    [InlineData("今天天气不错。", "今天天气不错")]
    [InlineData("今天天气不错？", "今天天气不错")]
    [InlineData("今天天气不错！", "今天天气不错")]
    [InlineData("今天天气不错；", "今天天气不错")]
    [InlineData("今天天气不错…", "今天天气不错")]
    [InlineData("今天天气不错.", "今天天气不错")]
    [InlineData("今天天气不错?", "今天天气不错")]
    [InlineData("今天天气不错!!", "今天天气不错")]
    [InlineData("今天天气不错。 ", "今天天气不错")]
    [InlineData("今天天气不错。？！", "今天天气不错")]
    [InlineData("今天天气不错", "今天天气不错")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 关闭保留时去掉末尾标点与空白(string? text, string expected)
    {
        Assert.Equal(expected, TrailingPunctuation.Apply(text, keep: false));
    }

    [Theory]
    [InlineData("今天天气不错。")]
    [InlineData("今天天气不错")]
    [InlineData("")]
    public void 保留时原样返回(string text)
    {
        Assert.Equal(text, TrailingPunctuation.Apply(text, keep: true));
    }

    [Fact]
    public void 结尾是引号时不动引号内的句号()
    {
        // 去掉“。”会留下不配对的引号，所以这种结尾整句原样保留。
        Assert.Equal("他说“走吧。”", TrailingPunctuation.Apply("他说“走吧。”", keep: false));
    }

    [Fact]
    public void 句中标点不受影响()
    {
        Assert.Equal(
            "第一句。第二句", TrailingPunctuation.Apply("第一句。第二句。", keep: false));
    }
}
