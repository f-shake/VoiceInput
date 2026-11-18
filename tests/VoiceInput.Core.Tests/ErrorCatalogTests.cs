using System.Text.RegularExpressions;
using VoiceInput.Core.Diagnostics;

namespace VoiceInput.Core.Tests;

public sealed class ErrorCatalogTests
{
    private static readonly Regex Emoji = new(
        @"[\uD800-\uDBFF]|[\u2600-\u27BF]|[\uFE0F\u200D\u20E3]",
        RegexOptions.Compiled);

    private static readonly Regex Chinese = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    public static TheoryData<string> Messages()
    {
        var data = new TheoryData<string>();
        foreach (var message in ErrorCatalog.AllMessages())
        {
            data.Add(message);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void MessageIsNonEmptyAndSingleLine(string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void MessageHasNoEmoji(string message)
    {
        Assert.False(Emoji.IsMatch(message), message);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void MessageAvoidsCornerQuotes(string message)
    {
        Assert.DoesNotContain("「", message, StringComparison.Ordinal);
        Assert.DoesNotContain("」", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void MessageContainsChinese(string message)
    {
        Assert.True(Chinese.IsMatch(message), message);
    }

    [Fact]
    public void StatusBarOverLimitIsTruncatedWithEllipsis()
    {
        var clamped = ErrorCatalog.ClampForStatus(new string('字', 40));

        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
        Assert.Equal(ErrorCatalog.StatusBarLimit, clamped.Length);
    }

    [Fact]
    public void StatusBarUnderLimitIsUnchanged()
    {
        Assert.Equal("短句", ErrorCatalog.ClampForStatus("短句"));
    }

    [Fact]
    public void PasteSuccessMessageIncludesCount()
    {
        Assert.Equal("已粘贴 7 字", ErrorCatalog.Pasted(7));
    }

    [Fact]
    public void ExceptionCarriesCodeMessageAndDetail()
    {
        var error = new VoiceInputException(ErrorCodes.PasteFailed, ErrorCatalog.PasteFailed, detail: "按键注入失败");

        Assert.Equal("E11", error.Code);
        Assert.Equal(ErrorCatalog.PasteFailed, error.UserMessage);
        Assert.Equal("按键注入失败", error.Detail);
        Assert.False(error.Retryable);
        Assert.Equal(ErrorCatalog.PasteFailed, error.Message);
    }
}
