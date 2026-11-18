using VoiceInput.Core.Config;

namespace VoiceInput.Core.Tests;

public sealed class StartupEntryTests
{
    private const string ExePath = @"C:\Users\tester\AppData\Local\VoiceInput\VoiceInput.exe";

    [Fact]
    public void PathWithSpacesIsQuoted()
    {
        var formatted = StartupEntry.Format(@"C:\Program Files\VoiceInput\VoiceInput.exe");

        Assert.StartsWith("\"", formatted, StringComparison.Ordinal);
        Assert.EndsWith("\"", formatted, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\VoiceInput\VoiceInput.exe", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattedValueMatchesItself()
    {
        Assert.True(StartupEntry.Matches(StartupEntry.Format(ExePath), ExePath));
    }

    [Fact]
    public void UnquotedPathAlsoMatches()
    {
        Assert.True(StartupEntry.Matches(ExePath, ExePath));
    }

    [Fact]
    public void DifferentCaseIsSameEntry()
    {
        Assert.True(StartupEntry.Matches(ExePath.ToLowerInvariant(), ExePath));
    }

    [Fact]
    public void MovedProgramCountsAsNotEnabled()
    {
        Assert.False(StartupEntry.Matches(@"D:\旧目录\VoiceInput.exe", ExePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValueCountsAsNotEnabled(string? value)
    {
        Assert.False(StartupEntry.Matches(value, ExePath));
    }

    [Fact]
    public void FormattingEmptyPathThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupEntry.Format("   "));
    }
}
