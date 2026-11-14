using VoiceInput.Core.Config;

namespace VoiceInput.Core.Tests;

public sealed class ApiKeyResolverTests
{
    private static string? Missing(string _) => null;

    [Fact]
    public void 设置里填的Key优先于环境变量()
    {
        var lookup = (string name) => name switch
        {
            "VOICEINPUT_API_KEY" => "sk-vi",
            "DEEPSEEK_API_KEY" => "sk-ds",
            _ => null,
        };

        var result = ApiKeyResolver.Resolve("sk-settings", lookup, Missing);

        Assert.Equal("sk-settings", result.Key);
        Assert.Equal(ApiKeyResolver.SourceFromSettings, result.Source);
    }

    [Fact]
    public void 设置清空后回退到进程环境变量()
    {
        var lookup = (string name) => name == "DEEPSEEK_API_KEY" ? "sk-ds" : null;

        var result = ApiKeyResolver.Resolve(string.Empty, lookup, Missing);

        Assert.Equal("sk-ds", result.Key);
        Assert.Contains("DEEPSEEK_API_KEY", result.Source);
    }

    [Fact]
    public void 第一个环境变量名优先于第二个()
    {
        var lookup = (string name) => name == "VOICEINPUT_API_KEY" ? "sk-vi" : name == "DEEPSEEK_API_KEY" ? "sk-ds" : null;

        var result = ApiKeyResolver.Resolve(null, lookup, Missing);

        Assert.Equal("sk-vi", result.Key);
        Assert.Contains("VOICEINPUT_API_KEY", result.Source);
    }

    [Fact]
    public void 进程环境变量缺失时回退用户级环境变量()
    {
        var user = (string name) => name == "DEEPSEEK_API_KEY" ? "sk-user" : null;

        var result = ApiKeyResolver.Resolve(null, Missing, user);

        Assert.Equal("sk-user", result.Key);
        Assert.Contains("用户环境变量", result.Source);
    }

    [Fact]
    public void 都没有时返回未找到()
    {
        var result = ApiKeyResolver.Resolve(null, Missing, Missing);

        Assert.Equal(string.Empty, result.Key);
        Assert.False(result.Found);
    }

    [Fact]
    public void 来源描述里不含Key本身()
    {
        var result = ApiKeyResolver.Resolve("sk-secret-value", Missing, Missing);

        Assert.DoesNotContain("sk-secret-value", result.Source);
    }

    [Fact]
    public void 空白值不会被当成有效的Key()
    {
        var lookup = (string _) => "   ";

        var result = ApiKeyResolver.Resolve("  ", lookup, Missing);

        Assert.False(result.Found);
    }
}
