using VoiceInput.Core.Text;

namespace VoiceInput.Core.Diagnostics;

/// <summary>
/// 带错误码的异常。Code 与运行手册的错误表一一对应，UserMessage 是能直接显示的
/// 一句人话；详细原因放 Detail，只写日志，不上状态条。
/// </summary>
public sealed class VoiceInputException : Exception
{
    public VoiceInputException(
        string code,
        string userMessage,
        bool retryable = false,
        string? detail = null,
        Exception? inner = null)
        : base(userMessage, inner)
    {
        Code = code;
        UserMessage = userMessage;
        Retryable = retryable;
        Detail = detail;
    }

    public string Code { get; }

    public string UserMessage { get; }

    public bool Retryable { get; }

    public string? Detail { get; }
}

public static class ErrorCodes
{    public const string ApiKeyMissing = "E01";
    public const string DeviceUnavailable = "E02";
    public const string RecordingTooShort = "E03";
    public const string ApiKeyRejected = "E04";
    public const string NetworkFailed = "E05";
    public const string RateLimited = "E06";
    public const string ServiceUnavailable = "E07";
    public const string AudioRejected = "E08";
    public const string UnparsableResponse = "E09";
    public const string ClipboardWriteFailed = "E10";
    public const string PasteFailed = "E11";
    public const string Busy = "E12";
    public const string Truncated = "E13";
    public const string ComboCancelled = "E14";
    public const string ConfigParseFailed = "E15";
    public const string ConfigValueInvalid = "E16";
    public const string ClipboardRestoreFailed = "E17";
    public const string RecordingInterrupted = "E18";
    public const string Unexpected = "E20";
    public const string AlreadyRunning = "E21";
    public const string OverlayFailed = "E22";
    public const string TrayFailed = "E23";
    public const string ConfigReadFailed = "E24";
    public const string LocalModelMissing = "E25";
}

/// <summary>
/// 全部面向用户的文案。集中在这里，是为了能逐条断言“中文、无 emoji、无直角引号”，
/// 散落到调用点就没有同一个断言入口了。
/// </summary>
public static class ErrorCatalog
{
    public const string ApiKeyMissing = "未配置 API Key，请检查 config.json。";
    public const string DeviceUnavailable = "无法打开麦克风，请在日志中查看设备列表。";
    public const string RecordingTooShort = "录音过短，已忽略。";
    public const string ApiKeyRejected = "API Key 无效或已失效。";
    public const string RateLimited = "请求过于频繁，请稍后再试。";
    public const string AudioRejected = "音频不被接受。";
    public const string NoSpeech = "没有识别到语音内容。";
    public const string ClipboardWriteFailed = "无法写入剪贴板，结果已写入日志。";
    public const string PasteFailed = "已复制，请按 Ctrl+V 粘贴。";
    public const string Busy = "正在转写，请稍候。";
    public const string ComboCancelled = "检测到组合键，已取消本次听写。";
    public const string ConfigParseFailed = "配置文件无法解析，请检查 config.json。";
    public const string ConfigReadFailed = "无法读取配置文件，请检查文件是否被占用。";
    public const string LocalModelMissing = "本地识别模型缺失或不完整，请检查模型目录。";
    public const string ClipboardRestoreFailed = "原剪贴板内容未能还原。";
    public const string RecordingInterrupted = "录音中断，本次听写已丢弃。";
    public const string Unexpected = "发生未预期的错误，详见日志。";
    public const string AlreadyRunning = "已有一个 VoiceInput 在运行。";
    public const string OverlayFailed = "仅提示音与日志可用。";
    public const string TrayFailed = "已改用 Ctrl+Alt+Q 退出。";
    public const string Recording = "录音中";
    public const string Transcribing = "转写中";

    /// <summary>超过 <paramref name="limit"/> 个字符的状态条文案要截断，免得挡住半个屏幕。</summary>
    public const int StatusBarLimit = 18;

    public static string NetworkFailed(int retries) => $"网络请求失败，已重试 {retries} 次。";

    public static string ServiceUnavailable(int retries) => $"服务暂时不可用，已重试 {retries} 次。";

    public static string Truncated(int maxSeconds) => $"录音超过 {maxSeconds} 秒，已按已录内容转写。";

    public static string ConfigInvalid(string name) => $"配置项 {name} 不合法。";

    public static string Pasted(int chars) => $"已粘贴 {chars} 字";

    /// <summary>
    /// 状态条放不下就截断：完整文案写日志，状态条只留一眼能看完的部分，
    /// 免得一条长错误把右下角挡住。
    /// </summary>
    public static string ClampForStatus(string text, int limit = StatusBarLimit) =>
        TextNormalizer.OneLine(text, limit);

    /// <summary>返回全部文案样本，供合规测试逐条断言。</summary>
    public static IReadOnlyList<string> AllMessages() => new[]
    {
        ApiKeyMissing,
        DeviceUnavailable,
        RecordingTooShort,
        ApiKeyRejected,
        RateLimited,
        AudioRejected,
        NoSpeech,
        ClipboardWriteFailed,
        PasteFailed,
        Busy,
        ComboCancelled,
        ConfigParseFailed,
        ConfigReadFailed,
        LocalModelMissing,
        ClipboardRestoreFailed,
        RecordingInterrupted,
        Unexpected,
        AlreadyRunning,
        OverlayFailed,
        TrayFailed,
        Recording,
        Transcribing,
        Pasted(12),
        NetworkFailed(2),
        ServiceUnavailable(2),
        Truncated(118),
        ConfigInvalid("maxSeconds"),
    };
}

/// <summary>
/// 状态机需要的日志接口。实现可以是按大小轮转的文件日志，也可以是测试里的假日志；
/// Core 不直接依赖文件系统，状态机才能在纯内存里跑。
/// </summary>
public interface IDiagnosticsLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);
}
