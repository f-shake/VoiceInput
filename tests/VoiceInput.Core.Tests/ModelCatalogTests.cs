using System.Net;
using VoiceInput.Core.Optimize;
using VoiceInput.Core.Tests.Fakes;

namespace VoiceInput.Core.Tests;

public sealed class ModelCatalogTests
{
    private const string TwoModels =
        """{"object":"list","data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"}]}""";

    [Fact]
    public async Task 请求发到models并带鉴权头()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal("https://api.example.com/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task 地址带结尾斜杠也能拼对()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        await catalog.ListAsync("https://api.example.com/", "sk-test", 5);

        Assert.Equal("https://api.example.com/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task 解析出模型标识()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, TwoModels);
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, models);
    }

    [Fact]
    public async Task 兼容根节点是字符串数组的端点()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """["model-a","model-b"]""");
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "model-a", "model-b" }, models);
    }

    [Fact]
    public async Task 空标识与重复项被过滤()
    {
        const string body =
            """{"data":[{"id":"a"},{"id":""},{"id":"a"},{"noId":"x"},{"id":"b"}]}""";
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, body);
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Equal(new[] { "a", "b" }, models);
    }

    [Fact]
    public async Task 形状不认识时返回空列表()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"object":"list"}""");
        using var catalog = new ModelCatalog(handler);

        var models = await catalog.ListAsync("https://api.example.com", "sk-test", 5);

        Assert.Empty(models);
    }

    [Fact]
    public async Task 非成功状态码抛错()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "{}");
        using var catalog = new ModelCatalog(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => catalog.ListAsync("https://api.example.com", "sk-test", 5));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task 超时抛取消异常()
    {
        using var catalog = new ModelCatalog(new HangingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.ListAsync("https://api.example.com", "sk-test", 0.1));
    }
}
