using System.Net;
using VoiceInput.Core.Optimize;
using VoiceInput.Core.Tests.Fakes;

namespace VoiceInput.Core.Tests;

public sealed class ModelCatalogTests
{
    private const string TwoModels =
        """{"object":"list","data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"}]}""";

    [Fact]
    public async Task RequestGoesToModelsWithAuthHeader()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal("https://api.example.com/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task BaseUrlWithTrailingSlashIsJoinedCorrectly()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        await catalog.ListAsync("https://api.example.com/", "sk-test", 5);

        Assert.Equal("https://api.example.com/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ModelIdsAreParsed()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, models);
    }

    [Fact]
    public async Task StringArrayRootIsSupported()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """["model-a","model-b"]""");
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "model-a", "model-b" }, models);
    }

    [Fact]
    public async Task EmptyIdsAndDuplicatesAreFiltered()
    {
        const string body =
            """{"data":[{"id":"a"},{"id":""},{"id":"a"},{"noId":"x"},{"id":"b"}]}""";
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, body);
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "a", "b" }, models);
    }

    [Fact]
    public async Task UnknownShapeReturnsEmptyList()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"object":"list"}""");
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Empty(models);
    }

    [Fact]
    public async Task NonSuccessStatusCodeThrows()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "{}");
        using var catalog = new ModelCatalog(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => catalog.ListAsync("https://api.example.com", "sk-test", 5));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task TimeoutThrowsOperationCanceled()
    {
        using var catalog = new ModelCatalog(new HangingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.ListAsync("https://api.example.com", "sk-test", 0.1));
    }
}
