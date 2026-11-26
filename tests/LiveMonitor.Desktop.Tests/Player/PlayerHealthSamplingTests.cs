using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

public sealed class PlayerHealthSamplingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 16, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Build_AllMissing_FallsBackToZeroAndSoftware()
    {
        var metrics = PlayerHealthSampling.Build(null, null, null, null, null, At);

        Assert.Equal(0, metrics.DecoderDroppedFrames);
        Assert.Equal(0, metrics.DroppedFrames);
        Assert.Equal(0, metrics.FramesPerSecond);
        Assert.False(metrics.HardwareDecoding);
        Assert.Null(metrics.HwdecCurrent);
        Assert.Equal(At, metrics.SampledAt);
    }

    [Fact]
    public void Build_PrefersContainerFpsOverVfFps()
    {
        var metrics = PlayerHealthSampling.Build(1, 2, 60, 15, "vaapi", At);

        Assert.Equal(60, metrics.FramesPerSecond);
        Assert.True(metrics.HardwareDecoding);
        Assert.Equal("vaapi", metrics.HwdecCurrent);
        Assert.Equal(1, metrics.DecoderDroppedFrames);
        Assert.Equal(2, metrics.DroppedFrames);
    }

    [Theory]
    [InlineData(null, null, 0d)]
    [InlineData(null, 15d, 15d)]
    [InlineData(60d, 15d, 60d)]
    [InlineData(0d, 15d, 0d)]
    [InlineData(59.94d, null, 59.94d)]
    public void ResolveFramesPerSecond_ContainerFirstThenVfFallback(
        double? containerFps,
        double? estimatedVfFps,
        double expected)
        => Assert.Equal(expected, PlayerHealthSampling.ResolveFramesPerSecond(containerFps, estimatedVfFps));

    [Theory]
    [InlineData("vaapi", true)]
    [InlineData("d3d11", true)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsHardwareDecoding_TreatsNoAndBlankAsSoftware(string? hwdec, bool expected)
        => Assert.Equal(expected, PlayerHealthSampling.IsHardwareDecoding(hwdec));
}
