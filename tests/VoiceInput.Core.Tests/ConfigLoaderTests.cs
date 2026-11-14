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
    public void 文件不存在时用全默认值()
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
    public void 空对象也用默认值()
    {
        var result = ConfigLoader.Load(Write("{}"));

        Assert.Equal(HotkeyNames.RightCtrl, result.Config.Hotkey);
        Assert.Equal(118, result.Config.MaxSeconds);
        Assert.True(result.Exists);
    }

    [Fact]
    public void 十二个字段都能覆盖()
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
    public void 允许注释与尾逗号()
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
    public void 未知字段只告警不报错()
    {
        var result = ConfigLoader.Load(Write("""{ "maxSeconds": 20, "maxSecnds": 99 }"""));

        Assert.Equal(20, result.Config.MaxSeconds);
        Assert.Single(result.Warnings);
        Assert.Contains("maxSecnds", result.Warnings[0]);
    }

    [Fact]
    public void JSON语法错误报E15()
    {
        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(Write("{\n  \"maxSeconds\": ,\n}")));

        Assert.Equal(ErrorCodes.ConfigParseFailed, error.Code);
        Assert.Contains("行", error.Detail);
    }

    [Fact]
    public void 顶层不是对象报E16()
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
    public void 非法取值报E16(string field, string rawValue)
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write($"{{ \"{field}\": {rawValue} }}")));

        Assert.Equal(ErrorCodes.ConfigValueInvalid, error.Code);
        Assert.Contains(field, error.UserMessage);
    }

    [Fact]
    public void 最短录音不能大于等于最长录音()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "minSeconds": 10, "maxSeconds": 10 }""")));

        Assert.Contains("minSeconds", error.UserMessage);
    }

    [Fact]
    public void 退避表长度不足要报错()
    {
        var error = Assert.Throws<VoiceInputException>(
            () => ConfigLoader.Load(Write("""{ "retries": 3, "retryBackoffSeconds": [0.1, 0.2] }""")));

        Assert.Contains("retryBackoffSeconds", error.UserMessage);
    }

    [Fact]
    public void 设备号可以写null()
    {
        var result = ConfigLoader.Load(Write("""{ "deviceNumber": null }"""));

        Assert.Null(result.Config.DeviceNumber);
    }

    [Fact]
    public void 热键别名收敛成规范名()
    {
        Assert.Equal("RightCtrl", ConfigLoader.Load(Write("""{ "hotkey": "right ctrl" }""")).Config.Hotkey);
        Assert.Equal("RightCtrl", ConfigLoader.Load(Write("""{ "hotkey": "RCTRL" }""")).Config.Hotkey);
        Assert.Equal("F12", ConfigLoader.Load(Write("""{ "hotkey": "f12" }""")).Config.Hotkey);
    }

    [Fact]
    public void 显式路径优先于其它查找位置()
    {
        var resolved = ConfigLoader.ResolvePath(@"C:\x\y.json", _directory, Path.GetTempPath());

        Assert.Equal(@"C:\x\y.json", resolved);
    }

    [Fact]
    public void exe同目录的配置优先于用户目录()
    {
        var beside = Path.Combine(_directory, "config.json");
        File.WriteAllText(beside, "{}");

        var resolved = ConfigLoader.ResolvePath(null, _directory, Path.GetTempPath());

        Assert.Equal(beside, resolved);
    }

    [Fact]
    public void 都没有时回落到用户目录()
    {
        var resolved = ConfigLoader.ResolvePath(null, _directory, @"C:\Users\x\AppData\Local");

        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local", "VoiceInput", "config.json"), resolved);
    }

    [Fact]
    public void 文件被独占时报E24而不是E15()
    {
        var path = Write("{}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Assert.Throws<VoiceInputException>(() => ConfigLoader.Load(path));

        // 被占用不等于语法错误，不能再引导用户去检查 JSON。
        Assert.Equal(ErrorCodes.ConfigReadFailed, error.Code);
    }
}
