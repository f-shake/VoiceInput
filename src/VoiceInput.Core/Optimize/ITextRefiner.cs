namespace VoiceInput.Core.Optimize;

/// <summary>
/// 结果优化的管线契约。状态机只认这个接口：它不关心请求发到哪、提示词怎么写。
/// 返回的文本已经是可直接粘贴的形态（单行、去控制字符）。
/// </summary>
public interface ITextRefiner
{
    /// <summary>日志里用的简短描述，例如 fix、punctuation。</summary>
    string Description { get; }

    /// <summary>优化一段转写文本。失败时抛 <see cref="Diagnostics.VoiceInputException"/>，由调用方决定回退。</summary>
    Task<string> RefineAsync(string text, CancellationToken cancellationToken);
}
