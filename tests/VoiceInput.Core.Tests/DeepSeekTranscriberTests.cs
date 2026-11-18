using System.Net;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Providers;
using VoiceInput.Core.Tests.Fakes;
using VoiceInput.Providers.DeepSeek;

namespace VoiceInput.Core.Tests;

public sealed class DeepSeekTranscriberTests
{
    private static AsrTuning Tuning(int retries = 2, double readTimeoutSeconds = 60) =>
        new(retries, new[] { 0.1, 0.2 }, 5.0, readTimeoutSeconds);

    private static DeepSeekTranscriber Client(
        FakeHttpHandler handler,
        List<double>? delays = null,
        int retries = 2,
        double readTimeoutSeconds = 60) =>
        new(
            "sk-test",
            "https://api.deepseek.com",
            Tuning(retries, readTimeoutSeconds),
            handler,
            (seconds, _) =>
            {
                delays?.Add(seconds);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task SuccessfulResponseIsParsedIntoTranscript()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"request_id":"req-1","text":"你好","duration_ms":900}""");

        using var client = Client(handler);
        var result = await client.TranscribeAsync(new byte[] { 1, 2, 3 }, "clip.wav");

        Assert.Equal("你好", result.Text);
        Assert.Equal(900, result.DurationMs);
        Assert.Equal("req-1", result.RequestId);
        Assert.True(result.ElapsedSeconds >= 0);
    }

    [Fact]
    public async Task RequestCarriesAuthHeaderAndMultipartFileName()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"text":"好"}""");
        using var client = Client(handler);

        await client.TranscribeAsync(new byte[] { 1 }, "clip.wav");

        var request = handler.LastRequest;
        Assert.NotNull(request);
        Assert.Equal("https://api.deepseek.com/v1/tools/asr", request!.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization?.Parameter);

        var body = handler.LastBody;
        Assert.NotNull(body);
        Assert.Contains("name=file", body, StringComparison.Ordinal);
        Assert.Contains("clip.wav", body, StringComparison.Ordinal);
        Assert.Contains("audio/wav", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewlinesInReturnedTextAreFlattened()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"request_id":"r","text":"第一句\n第二句","duration_ms":10}""");
        using var client = Client(handler);

        var result = await client.TranscribeAsync(new byte[] { 1 }, "a.wav");

        Assert.Equal("第一句 第二句", result.Text);
    }

    [Fact]
    public async Task MissingTextFieldReportsE09()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"request_id":"r"}""");
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.UnparsableResponse, error.Code);
    }

    [Fact]
    public async Task NonJsonResponseReportsE09()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "<html>502</html>");
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.UnparsableResponse, error.Code);
    }

    [Fact]
    public async Task NonStringTextReportsE09()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"text":123}""");
        using var client = Client(handler);

        await Assert.ThrowsAsync<VoiceInputException>(() => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));
    }

    [Fact]
    public async Task Status401IsNotRetried()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.Unauthorized)
            .Respond(HttpStatusCode.Unauthorized);
        var delays = new List<double>();
        using var client = Client(handler, delays);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.ApiKeyRejected, error.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task AudioErrorsAreNotRetried(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler().Respond(status).Respond(status).Respond(status);
        var delays = new List<double>();
        using var client = Client(handler, delays);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.AudioRejected, error.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Status429ReportsE06AfterRetriesExhausted()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.TooManyRequests);
        var delays = new List<double>();
        using var client = Client(handler, delays);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.RateLimited, error.Code);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(new[] { 0.1, 0.2 }, delays);
    }

    [Fact]
    public async Task Status503MentionsRetryCountAfterExhaustion()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable);
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.ServiceUnavailable, error.Code);
        Assert.Contains("已重试 2 次", error.UserMessage);
    }

    [Fact]
    public async Task ServerErrorThenSuccessReturnsResult()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.OK, """{"text":"好了"}""");
        using var client = Client(handler);

        var result = await client.TranscribeAsync(new byte[] { 1 }, "a.wav");

        Assert.Equal("好了", result.Text);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TransportFailureReportsE05AfterRetries()
    {
        var handler = new FakeHttpHandler()
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"));
        var delays = new List<double>();
        using var client = Client(handler, delays);

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.NetworkFailed, error.Code);
        Assert.Contains("已重试 2 次", error.UserMessage);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(new[] { 0.1, 0.2 }, delays);
    }

    [Fact]
    public async Task ReadTimeoutReportsE05()
    {
        using var client = new DeepSeekTranscriber(
            "sk-test", "https://api.deepseek.com", Tuning(readTimeoutSeconds: 0.05), new HangingHandler());

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(ErrorCodes.NetworkFailed, error.Code);
    }

    [Fact]
    public async Task ZeroRetriesSendsSingleRequest()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.ServiceUnavailable);
        using var client = Client(handler, retries: 0);

        await Assert.ThrowsAsync<VoiceInputException>(() => client.TranscribeAsync(new byte[] { 1 }, "a.wav"));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingDurationIsTreatedAsZero()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"text":"好"}""");
        using var client = Client(handler);

        var result = await client.TranscribeAsync(new byte[] { 1 }, "a.wav");

        Assert.Equal(0, result.DurationMs);
        Assert.Equal(string.Empty, result.RequestId);
    }
}
