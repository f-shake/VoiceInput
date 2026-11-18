using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Tests;

public sealed class RefineModesTests
{
    [Theory]
    [InlineData("none", RefineMode.None)]
    [InlineData("polish", RefineMode.Polish)]
    [InlineData("fix", RefineMode.Fix)]
    [InlineData("punctuation", RefineMode.Punctuation)]
    [InlineData("  FIX  ", RefineMode.Fix)]
    [InlineData("Punctuation", RefineMode.Punctuation)]
    public void AllFourValuesParseIgnoringCaseAndWhitespace(string raw, RefineMode expected)
    {
        Assert.True(RefineModes.TryParse(raw, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("poly")]
    [InlineData("不优化")]
    public void UnknownValueFallsBackToNone(string? raw)
    {
        Assert.False(RefineModes.TryParse(raw, out var mode));
        Assert.Equal(RefineMode.None, mode);
    }

    [Fact]
    public void ConfigValuesAndDisplayNamesAreDistinct()
    {
        var configValues = RefineModes.All.Select(RefineModes.ToConfigValue).ToList();
        var displayNames = RefineModes.All.Select(RefineModes.DisplayName).ToList();

        Assert.Equal(4, configValues.Count);
        Assert.Equal(configValues.Count, configValues.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(displayNames.Count, displayNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("none", RefineModes.ToConfigValue(RefineMode.None));
        Assert.Equal("polish", RefineModes.ToConfigValue(RefineMode.Polish));
        Assert.Equal("fix", RefineModes.ToConfigValue(RefineMode.Fix));
        Assert.Equal("punctuation", RefineModes.ToConfigValue(RefineMode.Punctuation));
        Assert.Equal("不优化", RefineModes.DisplayName(RefineMode.None));
        Assert.Equal("仅修复错别字和标点", RefineModes.DisplayName(RefineMode.Fix));
    }

    [Fact]
    public void DropdownStartsWithNone()
    {
        Assert.Equal(RefineMode.None, RefineModes.All[0]);
        Assert.Equal(
            new[] { RefineMode.None, RefineMode.Polish, RefineMode.Fix, RefineMode.Punctuation },
            RefineModes.All);
    }
}
