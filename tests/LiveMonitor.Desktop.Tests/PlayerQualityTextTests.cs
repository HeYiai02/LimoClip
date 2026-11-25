using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public class PlayerQualityTextTests
{
    [Theory]
    [InlineData("1080P", 60, "1080P · 60fps")]
    [InlineData("1080P", 30, "1080P · 30fps")]
    [InlineData("1080P", 59.94, "1080P · 60fps")]
    [InlineData("原画", 0, "原画")]
    [InlineData("原画", -1, "原画")]
    [InlineData("原画", double.NaN, "原画")]
    [InlineData(null, 60, "— · 60fps")]
    [InlineData("  1080P  ", 60, "1080P · 60fps")]
    public void Format_ShowsFpsOnlyWhenKnown(string? displayName, double fps, string expected)
    {
        Assert.Equal(expected, PlayerQualityText.Format(displayName, fps));
    }
}
