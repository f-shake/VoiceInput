using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Tests;

public sealed class RefinePromptTests
{
    public static TheoryData<RefineMode> Modes() =>
        new() { RefineMode.Polish, RefineMode.Fix, RefineMode.Punctuation };

    [Theory]
    [MemberData(nameof(Modes))]
    public void 三种方式都有公共约束与只输出结果的要求(RefineMode mode)
    {
        var prompt = RefinePrompt.Build(mode);

        Assert.Contains("只输出校对后的文字本身", prompt, StringComparison.Ordinal);
        Assert.Contains("不要解释", prompt, StringComparison.Ordinal);
        Assert.Contains("不得改变原意", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("「", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 三种方式的提示词互不相同()
    {
        var prompts = new[] { RefineMode.Polish, RefineMode.Fix, RefineMode.Punctuation }
            .Select(RefinePrompt.Build)
            .ToList();

        Assert.Equal(3, prompts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 仅修复标点明确禁止改动文字()
    {
        var prompt = RefinePrompt.Build(RefineMode.Punctuation);

        Assert.Contains("一个字都不要改", prompt, StringComparison.Ordinal);
        Assert.Contains("不要增删任何文字", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 修复错别字模式禁止删词与调语序()
    {
        var prompt = RefinePrompt.Build(RefineMode.Fix);

        Assert.Contains("不要调整语序", prompt, StringComparison.Ordinal);
        Assert.Contains("不要删除或增加任何词语", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 润色模式允许删语气词与微调语序()
    {
        var prompt = RefinePrompt.Build(RefineMode.Polish);

        Assert.Contains("语气词", prompt, StringComparison.Ordinal);
        Assert.Contains("可以微调语序", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 不优化没有提示词()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RefinePrompt.Build(RefineMode.None));
    }
}
