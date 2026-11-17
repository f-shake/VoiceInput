using VoiceInput.Core.Diagnostics;

namespace VoiceInput.Core.Optimize;

/// <summary>
/// “选了优化方式，但缺东西跑不了”时用的占位优化器：一调用就抛 E26，带上具体原因
/// （没填 Key、没选模型）。用空对象而不是返回 null，是为了让状态机仍然走“优化失败”
/// 分支——用户能在屏幕上看到橙色提示，而不是以为自己在用“不优化”。
/// </summary>
public sealed class UnavailableRefiner : ITextRefiner
{
    private readonly string _reason;

    public UnavailableRefiner(string description, string reason)
    {
        Description = description;
        _reason = reason;
    }

    public string Description { get; }

    public Task<string> RefineAsync(string text, CancellationToken cancellationToken) =>
        Task.FromException<string>(new VoiceInputException(
            ErrorCodes.AiOptimizeFailed,
            ErrorCatalog.AiOptimizeFailed,
            detail: _reason));
}
