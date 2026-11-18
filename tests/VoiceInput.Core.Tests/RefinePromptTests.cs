using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Tests;

public sealed class RefinePromptTests
{
    public static TheoryData<RefineMode> Modes() =>
        new() { RefineMode.Polish, RefineMode.Fix, RefineMode.Punctuation };

    [Theory]
    [MemberData(nameof(Modes))]
    public void AllModesShareCommonConstraintsAndOutputOnlyRule(RefineMode mode)
    {
        var prompt = RefinePrompt.Build(mode);

        Assert.Contains("只输出校对后的文字本身", prompt, StringComparison.Ordinal);
        Assert.Contains("不要解释", prompt, StringComparison.Ordinal);
        Assert.Contains("不得改变原意", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("「", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeModesProduceDistinctPrompts()
    {
        var prompts = new[] { RefineMode.Polish, RefineMode.Fix, RefineMode.Punctuation }
            .Select(RefinePrompt.Build)
            .ToList();

        Assert.Equal(3, prompts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PunctuationModeForbidsAnyTextChanges()
    {
        var prompt = RefinePrompt.Build(RefineMode.Punctuation);

        Assert.Contains("一个字都不要改", prompt, StringComparison.Ordinal);
        Assert.Contains("不要增删任何文字", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FixModeForbidsDeletingWordsAndReordering()
    {
        var prompt = RefinePrompt.Build(RefineMode.Fix);

        Assert.Contains("不要调整语序", prompt, StringComparison.Ordinal);
        Assert.Contains("不要删除或增加任何词语", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PolishModeAllowsRemovingFillersAndReordering()
    {
        var prompt = RefinePrompt.Build(RefineMode.Polish);

        Assert.Contains("语气词", prompt, StringComparison.Ordinal);
        Assert.Contains("可以微调语序", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneModeHasNoPrompt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RefinePrompt.Build(RefineMode.None));
    }
}
