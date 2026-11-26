using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class PluginVersionComparerTests
{
    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("1.9.9", "2.0.0", false)]
    public void IsNewer_ComparesNumericSegments(string candidate, string current, bool expected)
        => Assert.Equal(expected, PluginVersionComparer.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("1.2.1", "1.2", true)]
    [InlineData("1.2.0.0", "1.2", false)]
    [InlineData("1.2", "1.2.0.1", false)]
    [InlineData("1.2.0.1", "1.2", true)]
    public void IsNewer_PadsMissingSegmentsWithZero(string candidate, string current, bool expected)
        => Assert.Equal(expected, PluginVersionComparer.IsNewer(candidate, current));

    [Theory]
    [InlineData("v1.2.3", "1.2.2", true)]
    [InlineData("V1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "v1.2.2", true)]
    [InlineData(" 1.2.3 ", "\t1.2.2", true)]
    public void IsNewer_NormalizesPrefixAndWhitespace(string candidate, string current, bool expected)
        => Assert.Equal(expected, PluginVersionComparer.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2.3-beta", "1.2.3", false)]
    [InlineData("1.2.4-beta", "1.2.3", true)]
    [InlineData("1.2.3+build.5", "1.2.3", false)]
    [InlineData("2.0.0+abcdef", "1.9.9", true)]
    [InlineData("v1.2.3-rc.1+sha", "1.2.2", true)]
    public void IsNewer_IgnoresPrereleaseAndBuildMetadata(string candidate, string current, bool expected)
        => Assert.Equal(expected, PluginVersionComparer.IsNewer(candidate, current));

    [Theory]
    [InlineData("1..3", "1..2", true)]
    [InlineData("1..2", "1..2", false)]
    [InlineData("1..2", "1..2.1", false)]
    public void IsNewer_SkipsEmptySegments(string candidate, string current, bool expected)
        => Assert.Equal(expected, PluginVersionComparer.IsNewer(candidate, current));

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData("", "1.0.0")]
    [InlineData("1.0.0", "  ")]
    [InlineData("abc", "1.0.0")]
    [InlineData("1.0.0", "1.x")]
    [InlineData("-1.0.0", "0.9.0")]
    [InlineData("1.0.0", "-1.0.0")]
    public void IsNewer_InvalidInputsAreNeverNewer(string? candidate, string? current)
        => Assert.False(PluginVersionComparer.IsNewer(candidate, current));
}
