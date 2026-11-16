namespace VoiceInput.Core.Optimize;

/// <summary>
/// 三种优化方式各自的提示词。写成常量而不是让调用方拼，是为了“改了提示词”只有一个
/// 入口，也方便测试逐条断言模式之间的差异。
/// </summary>
public static class RefinePrompt
{
    /// <summary>三种模式共用的约束：模型很容易顺手解释两句，必须在提示词里堵住。</summary>
    public const string CommonRules =
        "你是中文语音听写结果的校对助手。输入是一段语音识别得到的文字，可能含同音字错误、"
        + "错别字和标点缺失。只输出校对后的文字本身：不要解释、不要加引号、不要分点、不要换行。"
        + "不得改变原意，不得增删实义信息。";

    public const string PolishTask =
        "任务：修正同音字与错别字，补全或修正标点；去掉“嗯、呃、啊、那个”这类无意义语气词与"
        + "重复词，并让句子通顺。可以微调语序，但不要改成人称或语气都变了的书面语。";

    public const string FixTask =
        "任务：只修正同音字与错别字、补全或修正标点。不要改写句子，不要调整语序，"
        + "不要删除或增加任何词语，包括语气词与重复词。";

    public const string PunctuationTask =
        "任务：只修正和补全标点符号。一个字都不要改，也不要增删任何文字，"
        + "包括语气词、重复词与空格。";

    public static string Build(RefineMode mode) => mode switch
    {
        RefineMode.Polish => CommonRules + PolishTask,
        RefineMode.Fix => CommonRules + FixTask,
        RefineMode.Punctuation => CommonRules + PunctuationTask,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "“不优化”没有提示词"),
    };
}
