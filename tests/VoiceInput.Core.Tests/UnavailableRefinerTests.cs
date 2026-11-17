using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Optimize;

namespace VoiceInput.Core.Tests;

public sealed class UnavailableRefinerTests
{
    [Fact]
    public async Task 调用时抛E26并带上原因()
    {
        var refiner = new UnavailableRefiner("polish", "没有选择模型");

        var error = await Assert.ThrowsAsync<VoiceInputException>(
            () => refiner.RefineAsync("你好", CancellationToken.None));

        Assert.Equal(ErrorCodes.AiOptimizeFailed, error.Code);
        Assert.Contains("没有选择模型", error.Detail, StringComparison.Ordinal);
        Assert.Equal("polish", refiner.Description);
    }
}
