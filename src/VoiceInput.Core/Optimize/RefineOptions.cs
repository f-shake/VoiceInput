namespace VoiceInput.Core.Optimize;

/// <summary>
/// 造优化器时用的参数。句末标点不在这里：那一项与“用哪个优化器”无关，由状态机在
/// 粘贴前统一处理，所以选“不优化”时它也照样生效。
/// </summary>
public sealed record RefineOptions(RefineMode Mode, string Model, double TimeoutSeconds, bool Thinking = false);
