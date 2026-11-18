using System.Net;
using System.Text.Json;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;
using VoiceInput.Core.Tests.Fakes;

namespace VoiceInput.Core.Tests;

public sealed class DeepSeekTextRefinerTests
{
    private static string Body(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    private static DeepSeekTextRefiner Build(
        HttpMessageHandler handler,
        RefineMode mode = RefineMode.Fix,
        double timeoutSeconds = 5,
        string baseUrl = "https://api.example.com",
        bool thinking = false)
    {
        return new DeepSeekTextRefiner(
            "sk-test",
            baseUrl,
            new RefineOptions(mode, "deepseek-chat", timeoutSeconds, thinking),
            handler);
    }

    [Fact]
    public async Task DefaultRequestBodyDisablesThinking()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好。"));
        using var refiner = Build(handler);

        await refiner.RefineAsync("你好", CancellationToken.None);

        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingThinkingDropsDisabledFlag()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好。"));
        using var refiner = Build(handler, thinking: true);

        await refiner.RefineAsync("你好", CancellationToken.None);

        Assert.DoesNotContain("thinking", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestToChatEndpointCarriesAuthModelAndZeroTemperature()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好。"));
        using var refiner = Build(handler);

        await refiner.RefineAsync("你好", CancellationToken.None);

        Assert.Equal("https://api.example.com/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("\"model\":\"deepseek-chat\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"temperature\":0", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("校对助手", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseUrlWithTrailingSlashIsJoinedCorrectly()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好。"));
        using var refiner = Build(handler, baseUrl: "https://api.example.com/");

        await refiner.RefineAsync("你好", CancellationToken.None);

        Assert.Equal("https://api.example.com/chat/completions", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SuccessReturnsRefinedTextFlattenedToOneLine()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好\n世界。"));
        using var refiner = Build(handler);

        var result = await refiner.RefineAsync("你好世界", CancellationToken.None);

        Assert.Equal("你好 世界。", result);
    }

    [Theory]
    [InlineData(RefineMode.Polish, "去掉“嗯、呃、啊、那个”")]
    [InlineData(RefineMode.Fix, "不要调整语序")]
    [InlineData(RefineMode.Punctuation, "只修正和补全标点符号")]
    public async Task EachModeSendsItsPromptToServer(RefineMode mode, string expected)
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好"));
        using var refiner = Build(handler, mode);

        await refiner.RefineAsync("你好", CancellationToken.None);

        Assert.Contains(expected, handler.LastBody, StringComparison.Ordinal);
        Assert.Equal(RefineModes.ToConfigValue(mode), refiner.Description);
    }

    [Fact]
    public async Task BlankResultIsRejected()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("   "));
        using var refiner = Build(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Equal(ErrorCodes.AiOptimizeFailed, error.Code);
        Assert.Contains("优化结果为空", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultMuchLongerThanInputIsRejected()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body(new string('阐', 60)));
        using var refiner = Build(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Contains("长度异常", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PunctuationOnlyModeRevertsTextChanges()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("今天天汽不错。"));
        using var refiner = Build(handler, RefineMode.Punctuation);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("今天天气不错。", CancellationToken.None));

        Assert.Contains("仅修复标点模式改动了文字", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PunctuationOnlyModeAcceptsPurePunctuationEdits()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好，世界。"));
        using var refiner = Build(handler, RefineMode.Punctuation);

        var result = await refiner.RefineAsync("你好世界", CancellationToken.None);

        Assert.Equal("你好，世界。", result);
    }

    [Fact]
    public async Task PunctuationOnlyModeRevertsLostCurrencySymbol()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("一共300元，"));
        using var refiner = Build(handler, RefineMode.Punctuation);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("一共¥300元", CancellationToken.None));

        Assert.Contains("仅修复标点模式改动了文字", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelRefusalIsNotPastedAsRefinedText()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, Body("抱歉，我无法完成这个请求。"));
        using var refiner = Build(handler, RefineMode.Fix);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("帮我把这句话改一下", CancellationToken.None));

        Assert.Contains("相差太大", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolishModeAcceptsRemovedFillers()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("我明天去北京。"));
        using var refiner = Build(handler, RefineMode.Polish);

        var result = await refiner.RefineAsync("嗯我那个明天去北京", CancellationToken.None);

        Assert.Equal("我明天去北京。", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task NonSuccessStatusCodeIsTreatedAsFailure(HttpStatusCode status, string expectedInDetail)
    {
        var handler = new FakeHttpHandler().Respond(status, "{}");
        using var refiner = Build(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Equal(ErrorCodes.AiOptimizeFailed, error.Code);
        Assert.Contains(expectedInDetail, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonResponseIsRejected()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "这不是 JSON");
        using var refiner = Build(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Contains("不是 JSON", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseMissingChoicesIsRejected()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"object":"chat"}""");
        using var refiner = Build(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Contains("choices[0].message.content", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutIsReportedWithTimeoutSeconds()
    {
        using var refiner = Build(new HangingHandler(), timeoutSeconds: 0.1);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Contains("读取超时", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseAfterDisposeThrowsObjectDisposed()
    {
        var refiner = Build(new FakeHttpHandler().Respond(HttpStatusCode.OK, Body("你好")));
        refiner.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));
    }
}
