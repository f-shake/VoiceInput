using System.Text.Json;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;

namespace VoiceInput.Core.Tests;

/// <summary>插件化改造引入的两个配置字段：provider 与 providerOptions。</summary>
public sealed class ProviderConfigTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "voiceinput-provider-" + Guid.NewGuid().ToString("N"));

    public ProviderConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void DefaultsToDeepSeekWithDefaultBaseUrl()
    {
        var config = ConfigLoader.Load(Write("{}")).Config;

        Assert.Equal(AppConfig.DefaultProvider, config.Provider);
        Assert.Equal(AppConfig.DefaultBaseUrl, config.ProviderOptions["baseUrl"]);
    }

    [Fact]
    public void ExplicitProviderAndOptionsAreRead()
    {
        var config = ConfigLoader.Load(Write("""
            {
              "provider": "whisper-local",
              "providerOptions": { "modelPath": "D:/models/ggml-base.bin", "language": "zh" }
            }
            """))
            .Config;

        Assert.Equal("whisper-local", config.Provider);
        Assert.Equal("D:/models/ggml-base.bin", config.ProviderOptions["modelPath"]);
        Assert.Equal("zh", config.ProviderOptions["language"]);
    }

    [Fact]
    public void BaseUrlOptionOverridesDefaultBaseUrl()
    {
        var config = ConfigLoader.Load(Write("""
            { "providerOptions": { "baseUrl": "https://proxy.example.com" } }
            """))
            .Config;

        Assert.Equal("https://proxy.example.com", config.ProviderOptions["baseUrl"]);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"has space\"")]
    [InlineData("\"带中文\"")]
    [InlineData("\"a/b\"")]
    [InlineData("\"../../../etc\"")]
    public void ProviderIdAcceptsOnlySafeCharacters(string raw)
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write($"{{ \"provider\": {raw} }}")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
        Assert.Contains("provider", error.UserMessage);
    }

    [Fact]
    public void ProviderIdIsLowercased()
    {
        Assert.Equal("deepseek", ConfigLoader.Load(Write("""{ "provider": "DeepSeek" }""")).Config.Provider);
    }

    [Fact]
    public void NonObjectOptionsFail()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "providerOptions": [] }""")));

        Assert.Contains("providerOptions", error.UserMessage);
    }

    [Fact]
    public void OptionValuesMustBeStrings()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "providerOptions": { "port": 8080 } }""")));

        // 用户文案只到“配置项不合法”这一层，具体哪个键写错在 Detail 里给日志看。
        Assert.Contains("providerOptions", error.UserMessage);
        Assert.Contains("port", error.Detail);
    }

    [Fact]
    public void ProviderAndOptionsSurviveSave()
    {
        var path = Path.Combine(_directory, "config.json");
        var saved = new AppConfig
        {
            Provider = "whisper-local",
            ProviderOptions = new Dictionary<string, string> { ["modelPath"] = "D:/m/ggml.bin" },
        };

        ConfigLoader.Save(path, saved);
        var loaded = ConfigLoader.Load(path).Config;

        Assert.Equal("whisper-local", loaded.Provider);
        Assert.Equal("D:/m/ggml.bin", loaded.ProviderOptions["modelPath"]);
    }

    [Fact]
    public void SaveKeepsHandwrittenFieldsInFile()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, """{ "myOwnNote": "别删我" }""");

        ConfigLoader.Save(path, new AppConfig { Provider = "deepseek" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("别删我", document.RootElement.GetProperty("myOwnNote").GetString());
        Assert.Equal("deepseek", document.RootElement.GetProperty("provider").GetString());
    }
}
