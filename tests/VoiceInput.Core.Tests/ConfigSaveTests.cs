using System.Text.Json;
using VoiceInput.Core.Config;

namespace VoiceInput.Core.Tests;

public sealed class ConfigSaveTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "voiceinput-save-" + Guid.NewGuid().ToString("N"));

    public ConfigSaveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathOf(string name = "config.json") => Path.Combine(_directory, name);

    private static AppConfig Sample() => new()
    {
        ApiKey = "sk-settings-value",
        Hotkey = "F9",
        DeviceNumber = 3,
        MaxSeconds = 60,
        MinSeconds = 0.5,
        Retries = 1,
        RetryBackoffSeconds = new[] { 0.25 },
        Beep = false,
        PasteDelayMilliseconds = 20,
        RestoreClipboard = false,
    };

    [Fact]
    public void 新文件写出全部二十个字段()
    {
        var path = PathOf();

        ConfigLoader.Save(path, Sample());

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        foreach (var name in new[]
                 {
                     "apiKey", "hotkey", "provider", "providerOptions", "deviceNumber",
                     "maxSeconds", "minSeconds", "connectTimeoutSeconds", "readTimeoutSeconds",
                     "retries", "retryBackoffSeconds", "beep", "pasteDelayMilliseconds",
                     "restoreClipboard", "aiOptimizeMode", "aiOptimizeApiKey",
                     "keepTrailingPunctuation", "aiOptimizeModel", "aiOptimizeBaseUrl",
                     "aiOptimizeTimeoutSeconds",
                 })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }
    }

    [Fact]
    public void 结果优化字段能写回并读回()
    {
        var path = PathOf();
        var saved = new AppConfig
        {
            AiOptimizeMode = "polish",
            AiOptimizeApiKey = "sk-optimize-key",
            KeepTrailingPunctuation = false,
            AiOptimizeModel = "deepseek-reasoner",
            AiOptimizeBaseUrl = "https://example.com/v1",
            AiOptimizeTimeoutSeconds = 15,
        };

        ConfigLoader.Save(path, saved);
        var loaded = ConfigLoader.Load(path).Config;

        Assert.Equal(saved.AiOptimizeMode, loaded.AiOptimizeMode);
        Assert.Equal(saved.AiOptimizeApiKey, loaded.AiOptimizeApiKey);
        Assert.Equal(saved.KeepTrailingPunctuation, loaded.KeepTrailingPunctuation);
        Assert.Equal(saved.AiOptimizeModel, loaded.AiOptimizeModel);
        Assert.Equal(saved.AiOptimizeBaseUrl, loaded.AiOptimizeBaseUrl);
        Assert.Equal(saved.AiOptimizeTimeoutSeconds, loaded.AiOptimizeTimeoutSeconds);
    }

    [Fact]
    public void 写回的配置能被重新读出来()
    {
        var path = PathOf();
        var saved = Sample();

        ConfigLoader.Save(path, saved);
        var loaded = ConfigLoader.Load(path).Config;

        Assert.Equal(saved.ApiKey, loaded.ApiKey);
        Assert.Equal(saved.Hotkey, loaded.Hotkey);
        Assert.Equal(saved.DeviceNumber, loaded.DeviceNumber);
        Assert.Equal(saved.MaxSeconds, loaded.MaxSeconds);
        Assert.Equal(saved.MinSeconds, loaded.MinSeconds);
        Assert.Equal(saved.Retries, loaded.Retries);
        Assert.Equal(saved.RetryBackoffSeconds, loaded.RetryBackoffSeconds);
        Assert.Equal(saved.Beep, loaded.Beep);
        Assert.Equal(saved.PasteDelayMilliseconds, loaded.PasteDelayMilliseconds);
        Assert.Equal(saved.RestoreClipboard, loaded.RestoreClipboard);
    }

    [Fact]
    public void 文件里不认识的字段会被保留()
    {
        var path = PathOf();
        File.WriteAllText(path, """{ "myOwnNote": "别删我", "hotkey": "RightCtrl" }""");

        ConfigLoader.Save(path, Sample());

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("别删我", document.RootElement.GetProperty("myOwnNote").GetString());
        Assert.Equal("F9", document.RootElement.GetProperty("hotkey").GetString());
    }

    [Fact]
    public void 旧文件不是合法JSON时直接重写()
    {
        var path = PathOf();
        File.WriteAllText(path, "{ 这不是 json");

        ConfigLoader.Save(path, Sample());

        Assert.Equal("F9", ConfigLoader.Load(path).Config.Hotkey);
    }

    [Fact]
    public void 目录不存在时会自动创建()
    {
        var path = Path.Combine(_directory, "深一层", "再一层", "config.json");

        ConfigLoader.Save(path, Sample());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 不留下临时文件()
    {
        var path = PathOf();

        ConfigLoader.Save(path, Sample());

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void 空Key与空设备写成null而不是省略()
    {
        var path = PathOf();
        var config = new AppConfig { ApiKey = string.Empty, DeviceNumber = null };

        ConfigLoader.Save(path, config);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(string.Empty, document.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deviceNumber").ValueKind);
    }

    [Fact]
    public void 写出的值类型与配置类型一致()
    {
        var path = PathOf();

        ConfigLoader.Save(path, Sample());

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("maxSeconds").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("beep").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("retryBackoffSeconds").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("hotkey").ValueKind);
    }

    [Fact]
    public void 覆盖保存不会累积旧的退避数组()
    {
        var path = PathOf();
        ConfigLoader.Save(path, new AppConfig { Retries = 2, RetryBackoffSeconds = new[] { 0.5, 1.5 } });

        ConfigLoader.Save(path, new AppConfig { Retries = 1, RetryBackoffSeconds = new[] { 0.2 } });

        var loaded = ConfigLoader.Load(path).Config;
        Assert.Equal(new[] { 0.2 }, loaded.RetryBackoffSeconds);
        Assert.Equal(1, loaded.Retries);
    }

    [Fact]
    public void 旧文件带注释时未知字段也不丢()
    {
        var path = PathOf();
        // 注释本身不会被保留（JsonNode 不保存注释），但用户手写的额外字段必须留住。
        File.WriteAllText(path, "{\n  // 我自己的备注\n  \"myOwnNote\": \"别删我\",\n}");

        ConfigLoader.Save(path, new AppConfig { Provider = "deepseek" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("别删我", document.RootElement.GetProperty("myOwnNote").GetString());
    }
}
