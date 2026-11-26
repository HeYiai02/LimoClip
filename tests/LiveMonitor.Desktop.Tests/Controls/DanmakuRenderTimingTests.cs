using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

public sealed class DanmakuRenderTimingTests
{
    [Fact]
    public void Constants_MatchSharedFrameLoopContract()
    {
        Assert.Equal(0.1, DanmakuRenderTiming.MaxFrameDelta);
        Assert.Equal(0.001, DanmakuRenderTiming.MinFrameDelta);
        Assert.Equal(1.0 / 60.0, DanmakuRenderTiming.FirstFrameDelta, 12);
        Assert.Equal(1200, DanmakuRenderTiming.FrameStallThresholdMilliseconds);
    }

    [Fact]
    public void ResolveFrameDelta_FirstFrame_UsesSixtyFpsEstimate()
    {
        var delta = DanmakuRenderTiming.ResolveFrameDelta(null, TimeSpan.FromSeconds(5));

        Assert.Equal(1.0 / 60.0, delta, 12);
    }

    [Theory]
    [InlineData(16.6667, 0.0166667)]
    [InlineData(33.3333, 0.0333333)]
    [InlineData(8.0, 0.008)]
    [InlineData(100.0, 0.1)]
    public void ResolveFrameDelta_NormalFrame_ReturnsElapsedSeconds(double milliseconds, double expectedSeconds)
    {
        var last = TimeSpan.Zero;
        var current = TimeSpan.FromMilliseconds(milliseconds);

        var delta = DanmakuRenderTiming.ResolveFrameDelta(last, current);

        Assert.Equal(expectedSeconds, delta, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(0.999)]
    public void ResolveFrameDelta_TinyGap_ClampsToMinimum(double milliseconds)
    {
        var last = TimeSpan.FromSeconds(1);
        var current = last + TimeSpan.FromMilliseconds(milliseconds);

        var delta = DanmakuRenderTiming.ResolveFrameDelta(last, current);

        Assert.Equal(DanmakuRenderTiming.MinFrameDelta, delta, 12);
    }

    [Theory]
    [InlineData(500.0)]
    [InlineData(1000.0)]
    [InlineData(60000.0)]
    public void ResolveFrameDelta_LongStall_ClampsToMaximum(double milliseconds)
    {
        var last = TimeSpan.Zero;
        var current = TimeSpan.FromMilliseconds(milliseconds);

        var delta = DanmakuRenderTiming.ResolveFrameDelta(last, current);

        Assert.Equal(DanmakuRenderTiming.MaxFrameDelta, delta, 12);
    }

    [Fact]
    public void ResolveFrameDelta_OutOfOrderTimestamp_ClampsToMinimum()
    {
        var last = TimeSpan.FromSeconds(10);
        var current = TimeSpan.FromSeconds(9.5);

        var delta = DanmakuRenderTiming.ResolveFrameDelta(last, current);

        Assert.Equal(DanmakuRenderTiming.MinFrameDelta, delta, 12);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-1000L)]
    public void IsFrameStalled_NoValidFrameTick_ReturnsFalse(long lastFrameTick)
    {
        Assert.False(DanmakuRenderTiming.IsFrameStalled(lastFrameTick, 5000));
    }

    [Theory]
    [InlineData(1000L, 2199L, false)]
    [InlineData(1000L, 2200L, true)]
    [InlineData(1000L, 2201L, true)]
    [InlineData(1000L, 900L, false)]
    public void IsFrameStalled_EvaluatesThreshold(long lastFrameTick, long nowTick, bool expected)
    {
        Assert.Equal(expected, DanmakuRenderTiming.IsFrameStalled(lastFrameTick, nowTick));
    }
}
