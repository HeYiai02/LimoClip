using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class DropGuardPolicyTests
{
    private static StreamQuality Quality(string id, int height, int fps)
        => new(id, $"{height}P", height, fps, "https://example.test/live", new Dictionary<string, string>());

    private static PlayerHealthMetrics Metrics(long decoderDrops = 0, long drops = 0, double fps = 30)
        => new(decoderDrops, drops, fps, false, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TotalDroppedFrames_SumsDecoderAndPipelineDrops()
    {
        Assert.Equal(12, DropGuardPolicy.TotalDroppedFrames(Metrics(5, 7)));
    }

    [Fact]
    public void TotalDroppedFrames_NullMetricsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => DropGuardPolicy.TotalDroppedFrames(null!));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(29.97, 29.97)]
    [InlineData(1.5, 1.5)]
    [InlineData(1, 30)]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(double.NaN, 30)]
    public void ResolveFramesPerSecond_FallsBackWhenUnusable(double input, double expected)
    {
        Assert.Equal(expected, DropGuardPolicy.ResolveFramesPerSecond(input), 3);
    }

    [Fact]
    public void ResolveDropRate_ComputesRateOverWindow()
    {
        var rate = DropGuardPolicy.ResolveDropRate(15, 30, 1);

        Assert.Equal(0.5, rate, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveDropRate_NonPositiveWindowIsZero(double elapsed)
    {
        Assert.Equal(0, DropGuardPolicy.ResolveDropRate(10, 30, elapsed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(double.NaN)]
    public void ResolveDropRate_UnusableFramesPerSecondIsZero(double fps)
    {
        Assert.Equal(0, DropGuardPolicy.ResolveDropRate(10, fps, 1));
    }

    [Fact]
    public void ResolveDropRate_NegativeDeltaClampsToZero()
    {
        Assert.Equal(0, DropGuardPolicy.ResolveDropRate(-10, 30, 1));
    }

    [Theory]
    [InlineData(0.0501, true)]
    [InlineData(0.05, false)]
    [InlineData(0.0499, false)]
    [InlineData(0, false)]
    public void IsBadCycle_AppliesStrictThreshold(double rate, bool expected)
    {
        Assert.Equal(expected, DropGuardPolicy.IsBadCycle(rate));
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 2, 3)]
    [InlineData(false, 5, 0)]
    [InlineData(false, 0, 0)]
    public void NextBadCycles_IncrementsOrResets(bool bad, int previous, int expected)
    {
        Assert.Equal(expected, DropGuardPolicy.NextBadCycles(bad, previous));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void ShouldDowngrade_RequiresConfiguredBadCycles(int badCycles, bool expected)
    {
        Assert.Equal(expected, DropGuardPolicy.ShouldDowngrade(badCycles));
    }

    [Fact]
    public void ChooseLowerQuality_NullCurrentReturnsNull()
    {
        Assert.Null(DropGuardPolicy.ChooseLowerQuality(null, [Quality("1080", 1080, 60)]));
    }

    [Fact]
    public void ChooseLowerQuality_NoLowerQualityReturnsNull()
    {
        StreamQuality[] available = [Quality("1080", 1080, 60), Quality("1440", 1440, 60)];

        Assert.Null(DropGuardPolicy.ChooseLowerQuality(Quality("720", 720, 30), available));
    }

    [Fact]
    public void ChooseLowerQuality_PicksHighestAvailableLowerTier()
    {
        StreamQuality[] available =
        [
            Quality("1080", 1080, 60),
            Quality("720", 720, 30),
            Quality("480", 480, 30)
        ];

        var lower = DropGuardPolicy.ChooseLowerQuality(Quality("1080", 1080, 60), available);

        Assert.NotNull(lower);
        Assert.Equal(720, lower.Height);
    }

    [Fact]
    public void ChooseLowerQuality_TieBreaksOnHigherFps()
    {
        StreamQuality[] available =
        [
            Quality("720-30", 720, 30),
            Quality("720-60", 720, 60),
            Quality("480", 480, 30)
        ];

        var lower = DropGuardPolicy.ChooseLowerQuality(Quality("1080", 1080, 60), available);

        Assert.NotNull(lower);
        Assert.Equal(60, lower.Fps);
    }

    [Fact]
    public void ChooseLowerQuality_EmptyListReturnsNull()
    {
        Assert.Null(DropGuardPolicy.ChooseLowerQuality(Quality("1080", 1080, 60), []));
    }

    [Fact]
    public void ChooseLowerQuality_NullAvailableThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => DropGuardPolicy.ChooseLowerQuality(Quality("1080", 1080, 60), null!));
    }

    [Fact]
    public void Thresholds_AreSingleSourceOfTruth()
    {
        Assert.Equal(0.05, DropGuardPolicy.BadDropRateThreshold);
        Assert.Equal(3, DropGuardPolicy.BadCyclesBeforeDowngrade);
        Assert.Equal(30, DropGuardPolicy.DefaultFramesPerSecond);
    }
}
