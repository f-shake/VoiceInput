using VoiceInput.Core.Config;

namespace VoiceInput.Core.Tests;

public sealed class AiEndpointTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("  https://api.deepseek.com  ", true)]
    [InlineData("https://", false)]
    [InlineData("https://a b", false)]
    [InlineData("ftp://api.deepseek.com", false)]
    [InlineData("api.deepseek.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 只接受完整的http或https绝对地址(string? value, bool expected)
    {
        Assert.Equal(expected, AiEndpoint.IsAbsoluteHttpUrl(value));
    }

    [Fact]
    public void 设置窗口与配置读取用同一套判断()
    {
        // “https://” 能过前缀检查但过不了 Uri 解析，两侧必须一致，否则写得进去读不回来。
        Assert.False(AiEndpoint.IsAbsoluteHttpUrl("https://"));
    }
}
