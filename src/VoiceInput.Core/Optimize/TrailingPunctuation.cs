namespace VoiceInput.Core.Optimize;

/// <summary>
/// 确定性去掉句末标点。这件事不交给模型：它只要有一次不听话，用户就会看到句号
/// 时有时无。
/// </summary>
public static class TrailingPunctuation
{
    /// <summary>句末标点与常见的“收尾”标点，全角半角都收。</summary>
    private static readonly char[] SentenceEnd =
    {
        '。', '．', '.', '！', '!', '？', '?', '；', ';', '…', '～', '~', '，', ',', '、',
    };

    /// <summary>
    /// <paramref name="keep"/> 为真时原样返回。末尾的引号或括号不动：只切引号**外面**
    /// 的标点，否则“他说‘走吧。’”会被削成不配对的引号。
    /// </summary>
    public static string Apply(string? text, bool keep)
    {
        if (keep || string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var trimmed = text.TrimEnd();
        var end = trimmed.Length;
        while (end > 0 && Array.IndexOf(SentenceEnd, trimmed[end - 1]) >= 0)
        {
            end--;
        }

        return trimmed[..end].TrimEnd();
    }
}
