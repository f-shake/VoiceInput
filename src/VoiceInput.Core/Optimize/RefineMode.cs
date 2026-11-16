namespace VoiceInput.Core.Optimize;

/// <summary>
/// 结果优化的处理强度。越往下改动越小，最保守的那个一个字都不许动。
/// “不优化”也在这里，而不是另设一个开关：一项设置只表达一件事，界面上就不会
/// 出现“开关关着、方式却选着”这种自相矛盾的状态。
/// </summary>
public enum RefineMode
{
    /// <summary>不优化：原样粘贴。句末标点开关与它无关，仍然独立生效。</summary>
    None,

    /// <summary>修复和润色：改错别字、补标点，并让句子读起来顺。</summary>
    Polish,

    /// <summary>仅修复错别字和标点：不改写句子，不删词、不调语序。</summary>
    Fix,

    /// <summary>仅修复标点：一个字都不改，只动标点。</summary>
    Punctuation,
}

/// <summary>
/// 模式在配置文件里的字符串、界面上的中文名之间的唯一映射。设置窗口、配置校验、
/// 提示词三处都从这里取，免得各自写一份字符串比较，将来加一个模式时漏改一处。
/// </summary>
public static class RefineModes
{
    public const string None = "none";
    public const string Polish = "polish";
    public const string Fix = "fix";
    public const string Punctuation = "punctuation";

    /// <summary>下拉框的显示顺序：先“不优化”，再按改动幅度从大到小。</summary>
    public static readonly IReadOnlyList<RefineMode> All = new[]
    {
        RefineMode.None,
        RefineMode.Polish,
        RefineMode.Fix,
        RefineMode.Punctuation,
    };

    public static bool TryParse(string? raw, out RefineMode mode)
    {
        mode = RefineMode.None;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case None:
                mode = RefineMode.None;
                return true;
            case Polish:
                mode = RefineMode.Polish;
                return true;
            case Fix:
                mode = RefineMode.Fix;
                return true;
            case Punctuation:
                mode = RefineMode.Punctuation;
                return true;
            default:
                return false;
        }
    }

    public static string ToConfigValue(RefineMode mode) => mode switch
    {
        RefineMode.None => None,
        RefineMode.Polish => Polish,
        RefineMode.Fix => Fix,
        RefineMode.Punctuation => Punctuation,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的优化方式"),
    };

    public static string DisplayName(RefineMode mode) => mode switch
    {
        RefineMode.None => "不优化",
        RefineMode.Polish => "修复和润色",
        RefineMode.Fix => "仅修复错别字和标点",
        RefineMode.Punctuation => "仅修复标点",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的优化方式"),
    };
}
