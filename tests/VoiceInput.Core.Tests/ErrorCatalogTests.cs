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
    public void 文案非空且是单行(string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void 文案不含表情符号(string message)
    {
        Assert.False(Emoji.IsMatch(message), message);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void 文案不用直角引号(string message)
    {
        Assert.DoesNotContain("「", message, StringComparison.Ordinal);
        Assert.DoesNotContain("」", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void 文案含中文(string message)
    {
        Assert.True(Chinese.IsMatch(message), message);
    }

    [Fact]
    public void 状态条超过上限被截断并加省略号()
    {
        var clamped = ErrorCatalog.ClampForStatus(new string('字', 40));

        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
        Assert.Equal(ErrorCatalog.StatusBarLimit, clamped.Length);
    }

    [Fact]
    public void 状态条没超上限时原样返回()
    {
        Assert.Equal("短句", ErrorCatalog.ClampForStatus("短句"));
    }

    [Fact]
    public void 粘贴成功文案带字数()
    {
        Assert.Equal("已粘贴 7 字", ErrorCatalog.Pasted(7));
    }

    [Fact]
    public void 异常携带错误码用户文案与细节()
    {
        var error = new VoiceInputException(ErrorCodes.PasteFailed, ErrorCatalog.PasteFailed, detail: "按键注入失败");

        Assert.Equal("E11", error.Code);
        Assert.Equal(ErrorCatalog.PasteFailed, error.UserMessage);
        Assert.Equal("按键注入失败", error.Detail);
        Assert.False(error.Retryable);
        Assert.Equal(ErrorCatalog.PasteFailed, error.Message);
    }
}
