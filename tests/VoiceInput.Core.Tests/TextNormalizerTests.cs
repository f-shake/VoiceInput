using VoiceInput.Core.Text;

namespace VoiceInput.Core.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  你好  ", "你好")]
    public void NullEmptyAndWhitespaceInputIsNormalized(string? raw, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(raw));
    }

    [Fact]
    public void NewlinesCollapseIntoSingleSpace()
    {
        Assert.Equal("第一行 第二行", TextNormalizer.Normalize("第一行\n第二行"));
    }

    [Fact]
    public void ConsecutiveNewlinesAndCarriageReturnsLeaveNoBlankLines()
    {
        Assert.Equal("甲 乙 丙", TextNormalizer.Normalize("甲\r\n\r\n乙\r丙"));
    }

    [Fact]
    public void TabsAndFullWidthSpacesCollapseToOneSpace()
    {
        Assert.Equal("甲 乙 丙 丁", TextNormalizer.Normalize("甲\t乙\u3000丙  丁"));
    }

    [Fact]
    public void ControlCharactersAreRemoved()
    {
        Assert.Equal("你好", TextNormalizer.Normalize("你\u0007好\u0000"));
    }

    [Fact]
    public void FiveThousandCharacterTextIsNotTruncated()
    {
        var raw = new string('词', 5000);

        Assert.Equal(5000, TextNormalizer.Normalize(raw).Length);
    }

    [Fact]
    public void EmojiCharactersArePreserved()
    {
        Assert.Equal("好耶🎉", TextNormalizer.Normalize("好耶🎉"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\n\t", true)]
    [InlineData("a", false)]
    [InlineData("。", false)]
    public void BlankTextIsDetected(string text, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.IsBlank(text));
    }

    [Fact]
    public void OneLineFlattensNewlinesAndTruncates()
    {
        Assert.Equal("甲 乙 丙", TextNormalizer.OneLine("甲\n乙\t丙"));

        var clamped = TextNormalizer.OneLine(new string('到', 10), limit: 5);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
        Assert.Equal(5, clamped.Length);
    }
}
