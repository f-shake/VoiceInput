using System.Text;
using System.Text.RegularExpressions;

namespace VoiceInput.Core.Text;

/// <summary>
/// 把服务端返回的文本变成可以直接粘贴的单行文本。
/// </summary>
public static partial class TextNormalizer
{
    public const string Ellipsis = "…";

    /// <summary>
    /// 去掉控制字符、折掉多余空白、把换行压成空格。
    /// 换行必须压掉：粘贴到聊天框这类单行输入位时，换行等于按下发送，
    /// 半句话就发出去了。代价是长段落的排版丢失，但听写本来就按短句用。
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = ControlChars().Replace(raw, string.Empty);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = HorizontalSpaces().Replace(text, " ");
        text = Newlines().Replace(text, " ");
        return text.Trim();
    }

    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// 压成一行并截断，用于把任意原因塞进状态条或日志。
    /// 截断时避开代理对，否则切在 emoji 中间会得到半个字符。
    /// </summary>
    public static string OneLine(string? text, int limit = 200)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = ControlChars().Replace(text, string.Empty);
        flat = AllWhitespace().Replace(flat, " ").Trim();
        if (flat.Length <= limit)
        {
            return flat;
        }

        var cut = Math.Max(limit - 1, 0);
        if (cut > 0 && char.IsHighSurrogate(flat[cut - 1]))
        {
            cut--;
        }

        return string.Concat(flat.AsSpan(0, cut), Ellipsis);
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"[ \t\u3000\u00A0]+")]
    private static partial Regex HorizontalSpaces();

    [GeneratedRegex(@"\n+")]
    private static partial Regex Newlines();

    [GeneratedRegex(@"[\s]+")]
    private static partial Regex AllWhitespace();
}
