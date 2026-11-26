using System;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Models;

public sealed class LiveClipSafetyTests
{
    [Fact]
    public void LiveEdgeSafetyMargin_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LiveClipSafety.LiveEdgeSafetyMargin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    [InlineData(1)]
    public void ComputeSafeDuration_returns_zero_for_non_positive_duration(int seconds)
    {
        Assert.Equal(TimeSpan.Zero, LiveClipSafety.ComputeSafeDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ComputeSafeDuration_returns_zero_for_sub_millisecond_duration()
    {
        Assert.Equal(TimeSpan.Zero, LiveClipSafety.ComputeSafeDuration(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ComputeSafeDuration_returns_zero_at_exact_margin()
    {
        Assert.Equal(TimeSpan.Zero, LiveClipSafety.ComputeSafeDuration(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ComputeSafeDuration_returns_zero_when_just_below_margin()
    {
        var recorded = TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1);

        Assert.Equal(TimeSpan.Zero, LiveClipSafety.ComputeSafeDuration(recorded));
    }

    [Fact]
    public void ComputeSafeDuration_subtracts_margin_when_just_above_margin()
    {
        var recorded = TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1);

        Assert.Equal(TimeSpan.FromMilliseconds(1), LiveClipSafety.ComputeSafeDuration(recorded));
    }

    [Fact]
    public void ComputeSafeDuration_subtracts_margin_for_typical_recording()
    {
        Assert.Equal(TimeSpan.FromSeconds(25), LiveClipSafety.ComputeSafeDuration(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ComputeSafeDuration_subtracts_margin_for_long_recording()
    {
        Assert.Equal(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(55),
            LiveClipSafety.ComputeSafeDuration(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ComputeSafeDuration_subtracts_margin_for_max_value()
    {
        Assert.Equal(TimeSpan.MaxValue - LiveClipSafety.LiveEdgeSafetyMargin,
            LiveClipSafety.ComputeSafeDuration(TimeSpan.MaxValue));
    }

    [Fact]
    public void ComputeSafeDuration_is_monotonic_across_margin_boundary()
    {
        TimeSpan previous = TimeSpan.Zero;
        for (var ms = 0; ms <= 12000; ms += 250)
        {
            var safe = LiveClipSafety.ComputeSafeDuration(TimeSpan.FromMilliseconds(ms));
            Assert.True(safe >= previous, $"{ms}ms 的安全时长回退");
            previous = safe;
        }
    }
}
