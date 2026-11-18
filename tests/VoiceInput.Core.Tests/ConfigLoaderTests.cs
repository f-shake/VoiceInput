using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;

namespace VoiceInput.Core.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "voiceinput-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

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
    public void MissingFileUsesAllDefaults()
    {
        var result = ConfigLoader.Load(Path.Combine(_directory, "没有这个文件.json"));

        Assert.Equal(AppConfig.MaxSecondsLimit, result.Config.MaxSeconds);
        Assert.Equal(0.3, result.Config.MinSeconds);
        Assert.Equal(2, result.Config.Retries);
        Assert.True(result.Config.Beep);
        Assert.Empty(result.Warnings);
        Assert.False(result.Exists);
    }

    [Fact]
    public void EmptyObjectAlsoUsesDefaults()
    {
        var result = ConfigLoader.Load(Write("{}"));

        Assert.Equal(HotkeyNames.RightCtrl, result.Config.Hotkey);
        Assert.Equal(118, result.Config.MaxSeconds);
        Assert.True(result.Exists);
    }

    [Fact]
    public void AllTwelveFieldsCanBeOverridden()
    {
        var result = ConfigLoader.Load(Write("""
            {
              "apiKey": "sk-test",
              "hotkey": "f9",
              "deviceNumber": 2,
              "maxSeconds": 30,
              "minSeconds": 0.5,
              "connectTimeoutSeconds": 3,
              "readTimeoutSeconds": 20,
              "retries": 1,
              "retryBackoffSeconds": [0.2],
              "beep": false,
              "pasteDelayMilliseconds": 10,
              "restoreClipboard": false
            }
            """));

        var config = result.Config;
        Assert.Equal("sk-test", config.ApiKey);
        Assert.Equal("F9", config.Hotkey);
        Assert.Equal(2, config.DeviceNumber);
        Assert.Equal(30, config.MaxSeconds);
        Assert.Equal(0.5, config.MinSeconds);
        Assert.Equal(3, config.ConnectTimeoutSeconds);
        Assert.Equal(20, config.ReadTimeoutSeconds);
        Assert.Equal(1, config.Retries);
        Assert.Equal(new[] { 0.2 }, config.RetryBackoffSeconds);
        Assert.False(config.Beep);
        Assert.Equal(10, config.PasteDelayMilliseconds);
        Assert.False(config.RestoreClipboard);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        var result = ConfigLoader.Load(Write("""
            {
              // 手写配置里注释很常见
              "maxSeconds": 20,
            }
            """));

        Assert.Equal(20, result.Config.MaxSeconds);
    }

    [Fact]
    public void UnknownFieldOnlyWarns()
    {
        var result = ConfigLoader.Load(Write("""{ "maxSeconds": 20, "maxSecnds": 99 }"""));

        Assert.Equal(20, result.Config.MaxSeconds);
        Assert.Single(result.Warnings);
        Assert.Contains("maxSecnds", result.Warnings[0]);
    }

    [Fact]
    public void JsonSyntaxErrorReportsE15()
    {
        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(Write("{\n  \"maxSeconds\": ,\n}")));

        Assert.Equal(ErrorCodes.ConfigParseFailed, error.Code);
        Assert.Contains("行", error.Detail);
    }

    [Fact]
    public void NonObjectRootReportsE16()
    {
        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(Write("[1,2]")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
    }

    [Theory]
    [InlineData("maxSeconds", "0")]
    [InlineData("maxSeconds", "119")]
    [InlineData("maxSeconds", "\"30\"")]
    [InlineData("minSeconds", "-1")]
    [InlineData("minSeconds", "11")]
    [InlineData("retries", "6")]
    [InlineData("pasteDelayMilliseconds", "1001")]
    [InlineData("connectTimeoutSeconds", "0.1")]
    [InlineData("readTimeoutSeconds", "121")]
    [InlineData("beep", "\"true\"")]
    [InlineData("deviceNumber", "-1")]
    [InlineData("deviceNumber", "true")]
    [InlineData("retryBackoffSeconds", "[0.1]")]
    [InlineData("retryBackoffSeconds", "[11]")]
    [InlineData("retryBackoffSeconds", "[\"0.5\"]")]
    [InlineData("hotkey", "\"left ctrl\"")]
    [InlineData("hotkey", "\"\"")]
    [InlineData("aiOptimizeMode", "\"poly\"")]
    [InlineData("aiOptimizeMode", "\"\"")]
    [InlineData("aiOptimizeBaseUrl", "\"ftp://x\"")]
    [InlineData("aiOptimizeBaseUrl", "\"not a url\"")]
    [InlineData("aiOptimizeTimeoutSeconds", "0")]
    [InlineData("aiOptimizeTimeoutSeconds", "61")]
    [InlineData("keepTrailingPunctuation", "\"false\"")]
    [InlineData("aiOptimizeApiKey", "123")]
    [InlineData("aiOptimizeMode", "true")]
    [InlineData("aiOptimizeThinking", "1")]
    public void InvalidValueReportsE16(string field, string rawValue)
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write($"{{ \"{field}\": {rawValue} }}")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
        Assert.Contains(field, error.UserMessage);
    }

    [Fact]
    public void MinSecondsMustBeLessThanMaxSeconds()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "minSeconds": 10, "maxSeconds": 10 }""")));

        Assert.Contains("minSeconds", error.UserMessage);
    }

    [Fact]
    public void ShortBackoffTableFails()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "retries": 3, "retryBackoffSeconds": [0.1, 0.2] }""")));

        Assert.Contains("retryBackoffSeconds", error.UserMessage);
    }

    [Fact]
    public void DeviceNumberAcceptsNull()
    {
        var result = ConfigLoader.Load(Write("""{ "deviceNumber": null }"""));

        Assert.Null(result.Config.DeviceNumber);
    }

    [Fact]
    public void HotkeyAliasesCollapseToCanonicalName()
    {
        Assert.Equal("RightCtrl", ConfigLoader.Load(Write("""{ "hotkey": "right ctrl" }""")).Config.Hotkey);
        Assert.Equal("RightCtrl", ConfigLoader.Load(Write("""{ "hotkey": "RCTRL" }""")).Config.Hotkey);
        Assert.Equal("F12", ConfigLoader.Load(Write("""{ "hotkey": "f12" }""")).Config.Hotkey);
    }

    [Fact]
    public void MissingOverlayUsesBottomCenterDefaults()
    {
        var config = ConfigLoader.Load(Write("""{ "hotkey": "F9" }""")).Config;

        Assert.Equal(AppConfig.DefaultOverlayX, config.OverlayX);
        Assert.Equal(AppConfig.DefaultOverlayY, config.OverlayY);
    }

    [Fact]
    public void OverlayPositionCanBeReadBack()
    {
        var config = ConfigLoader.Load(Write("""{ "overlay": { "x": 0.2, "y": 0.7 } }""")).Config;

        Assert.Equal(0.2, config.OverlayX);
        Assert.Equal(0.7, config.OverlayY);
    }

    [Fact]
    public void OverlayOutOfRangeIsClampedToBounds()
    {
        var config = ConfigLoader.Load(Write("""{ "overlay": { "x": 2, "y": -1 } }""")).Config;

        Assert.Equal(AppConfig.OverlayMaxFraction, config.OverlayX);
        Assert.Equal(AppConfig.OverlayMinFraction, config.OverlayY);
    }

    [Fact]
    public void HalfOverlayKeepsOtherHalfDefault()
    {
        var config = ConfigLoader.Load(Write("""{ "overlay": { "x": 0.1 } }""")).Config;

        Assert.Equal(0.1, config.OverlayX);
        Assert.Equal(AppConfig.DefaultOverlayY, config.OverlayY);
    }

    [Fact]
    public void NonObjectOverlayReportsE16()
    {
        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(Write("""{ "overlay": 1 }""")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
        Assert.Contains("overlay", error.UserMessage);
    }

    [Fact]
    public void MisspelledOverlayKeyWarnsInsteadOfFallingBackSilently()
    {
        var loaded = ConfigLoader.Load(Write("""{ "overlay": { "X": 0.2, "y": 0.7 } }"""));

        Assert.Equal(AppConfig.DefaultOverlayX, loaded.Config.OverlayX);
        Assert.Equal(0.7, loaded.Config.OverlayY);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("overlay.X", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitPathWinsOverOtherLocations()
    {
        var resolved = ConfigLoader.ResolvePath(@"C:\x\y.json", _directory, Path.GetTempPath());

        Assert.Equal(@"C:\x\y.json", resolved);
    }

    [Fact]
    public void ConfigBesideExeWinsOverUserDirectory()
    {
        var beside = Path.Combine(_directory, "config.json");
        File.WriteAllText(beside, "{}");

        var resolved = ConfigLoader.ResolvePath(null, _directory, Path.GetTempPath());

        Assert.Equal(beside, resolved);
    }

    [Fact]
    public void FallsBackToUserDirectoryWhenNoneExists()
    {
        var resolved = ConfigLoader.ResolvePath(null, _directory, @"C:\Users\x\AppData\Local");

        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local", "VoiceInput", "config.json"), resolved);
    }

    [Fact]
    public void ExclusiveFileReportsE24NotE15()
    {
        var path = Write("{}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(path));

        // 被占用不等于语法错误，不能再引导用户去检查 JSON。
        Assert.Equal(ErrorCodes.ConfigReadFailed, error.Code);
    }

    [Fact]
    public void AllSixOptimizeFieldsReadCorrectly()
    {
        var config = ConfigLoader.Load(Write("""
            {
              "aiOptimizeMode": "punctuation",
              "aiOptimizeApiKey": "sk-optimize",
              "keepTrailingPunctuation": false,
              "aiOptimizeModel": "deepseek-reasoner",
              "aiOptimizeBaseUrl": "https://example.com/v1",
              "aiOptimizeTimeoutSeconds": 12.5,
              "aiOptimizeThinking": true
            }
            """)).Config;

        Assert.Equal("punctuation", config.AiOptimizeMode);
        Assert.Equal("sk-optimize", config.AiOptimizeApiKey);
        Assert.False(config.KeepTrailingPunctuation);
        Assert.Equal("deepseek-reasoner", config.AiOptimizeModel);
        Assert.Equal("https://example.com/v1", config.AiOptimizeBaseUrl);
        Assert.Equal(12.5, config.AiOptimizeTimeoutSeconds);
        Assert.True(config.AiOptimizeThinking);
    }

    [Fact]
    public void OptimizeDefaultsToNoneAndEmptyModel()
    {
        var config = ConfigLoader.Load(Write("{}")).Config;

        Assert.Equal("none", config.AiOptimizeMode);
        Assert.Equal(string.Empty, config.AiOptimizeApiKey);
        Assert.True(config.KeepTrailingPunctuation);
        Assert.Equal(string.Empty, config.AiOptimizeModel);
        Assert.Equal("https://api.deepseek.com", config.AiOptimizeBaseUrl);
        Assert.Equal(20.0, config.AiOptimizeTimeoutSeconds);
        Assert.False(config.AiOptimizeThinking);
    }

    [Fact]
    public void ModelNameAcceptsEmptyString()
    {
        var config = ConfigLoader.Load(Write("""{ "aiOptimizeModel": "" }""")).Config;

        Assert.Equal(string.Empty, config.AiOptimizeModel);
    }

    [Fact]
    public void ModelNameOverSixtyFourCharsReportsE16()
    {
        var tooLong = new string('m', 65);
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write($$"""{ "aiOptimizeModel": "{{tooLong}}" }""")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
        Assert.Contains("aiOptimizeModel", error.UserMessage);
    }
}
