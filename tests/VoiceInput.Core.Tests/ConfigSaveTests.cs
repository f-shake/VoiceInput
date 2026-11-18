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
        OverlayX = 0.25,
        OverlayY = 0.8,
    };

    [Fact]
    public void NewFileWritesAllTwentyTwoFields()
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
                     "restoreClipboard", "overlay", "aiOptimizeMode", "aiOptimizeApiKey",
                     "keepTrailingPunctuation", "aiOptimizeModel", "aiOptimizeBaseUrl",
                     "aiOptimizeTimeoutSeconds", "aiOptimizeThinking",
                 })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }
    }

    [Fact]
    public void OptimizeFieldsSurviveSaveAndLoad()
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
            AiOptimizeThinking = true,
        };

        ConfigLoader.Save(path, saved);
        var loaded = ConfigLoader.Load(path).Config;

        Assert.Equal(saved.AiOptimizeMode, loaded.AiOptimizeMode);
        Assert.Equal(saved.AiOptimizeApiKey, loaded.AiOptimizeApiKey);
        Assert.Equal(saved.KeepTrailingPunctuation, loaded.KeepTrailingPunctuation);
        Assert.Equal(saved.AiOptimizeModel, loaded.AiOptimizeModel);
        Assert.Equal(saved.AiOptimizeBaseUrl, loaded.AiOptimizeBaseUrl);
        Assert.Equal(saved.AiOptimizeTimeoutSeconds, loaded.AiOptimizeTimeoutSeconds);
        Assert.Equal(saved.AiOptimizeThinking, loaded.AiOptimizeThinking);
    }

    [Fact]
    public void SavedConfigCanBeReadBack()
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
        Assert.Equal(saved.OverlayX, loaded.OverlayX);
        Assert.Equal(saved.OverlayY, loaded.OverlayY);
    }

    [Fact]
    public void UnknownFieldsArePreserved()
    {
        var path = PathOf();
        File.WriteAllText(path, """{ "myOwnNote": "别删我", "hotkey": "RightCtrl" }""");

        ConfigLoader.Save(path, Sample());

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("别删我", document.RootElement.GetProperty("myOwnNote").GetString());
        Assert.Equal("F9", document.RootElement.GetProperty("hotkey").GetString());
    }

    [Fact]
    public void InvalidOldFileIsOverwritten()
    {
        var path = PathOf();
        File.WriteAllText(path, "{ 这不是 json");

        ConfigLoader.Save(path, Sample());

        Assert.Equal("F9", ConfigLoader.Load(path).Config.Hotkey);
    }

    [Fact]
    public void SavingOverlayKeepsUserSubKeys()
    {
        var path = PathOf();
        File.WriteAllText(path, """{ "overlay": { "x": 0.1, "y": 0.1, "myNote": "别删我" } }""");

        ConfigLoader.Save(path, new AppConfig { OverlayX = 0.3, OverlayY = 0.8 });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var overlay = document.RootElement.GetProperty("overlay");
        Assert.Equal(0.3, overlay.GetProperty("x").GetDouble());
        Assert.Equal(0.8, overlay.GetProperty("y").GetDouble());
        Assert.Equal("别删我", overlay.GetProperty("myNote").GetString());
    }

    [Fact]
    public void MissingDirectoryIsCreatedAutomatically()
    {
        var path = Path.Combine(_directory, "深一层", "再一层", "config.json");

        ConfigLoader.Save(path, Sample());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NoTempFileIsLeftBehind()
    {
        var path = PathOf();

        ConfigLoader.Save(path, Sample());

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void EmptyKeyAndDeviceAreWrittenAsNullNotEmpty()
    {
        var path = PathOf();
        var config = new AppConfig { ApiKey = string.Empty, DeviceNumber = null };

        ConfigLoader.Save(path, config);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(string.Empty, document.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("deviceNumber").ValueKind);
    }

    [Fact]
    public void WrittenValueTypesMatchConfigTypes()
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
    public void OverwriteSaveDoesNotAccumulateBackoffArray()
    {
        var path = PathOf();
        ConfigLoader.Save(path, new AppConfig { Retries = 2, RetryBackoffSeconds = new[] { 0.5, 1.5 } });

        ConfigLoader.Save(path, new AppConfig { Retries = 1, RetryBackoffSeconds = new[] { 0.2 } });

        var loaded = ConfigLoader.Load(path).Config;
        Assert.Equal(new[] { 0.2 }, loaded.RetryBackoffSeconds);
        Assert.Equal(1, loaded.Retries);
    }

    [Fact]
    public void UnknownFieldsSurviveInCommentedOldFile()
    {
        var path = PathOf();
        // 注释本身不会被保留（JsonNode 不保存注释），但用户手写的额外字段必须留住。
        File.WriteAllText(path, "{\n  // 我自己的备注\n  \"myOwnNote\": \"别删我\",\n}");

        ConfigLoader.Save(path, new AppConfig { Provider = "deepseek" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("别删我", document.RootElement.GetProperty("myOwnNote").GetString());
    }
}
