namespace VoiceInput.Core.Asr;

/// <summary>
/// 一次转写的结果。ElapsedSeconds 是客户端实际等到的秒数（含重试），
/// 与 DurationMs（服务端报告的音频时长）不是一回事。
/// </summary>
public readonly record struct AsrResult(string Text, int DurationMs, string RequestId, double ElapsedSeconds);
