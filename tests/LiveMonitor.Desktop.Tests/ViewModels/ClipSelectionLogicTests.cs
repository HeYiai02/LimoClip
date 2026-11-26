using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipSelectionLogicTests
{
    [Theory]
    [InlineData(true, 8.0, 10.0, 8.0)]
    [InlineData(true, 0.0, 10.0, 10.0)]
    [InlineData(false, 8.0, 10.0, 10.0)]
    [InlineData(false, 0.0, 10.0, 10.0)]
    public void UpperBound_UsesSafeClipDurationOnlyInLiveSession(
        bool isLive,
        double safeSeconds,
        double durationSeconds,
        double expectedSeconds)
    {
        var upper = ClipSelectionLogic.UpperBound(
            isLive,
            TimeSpan.FromSeconds(safeSeconds),
            TimeSpan.FromSeconds(durationSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), upper);
    }

    [Theory]
    [InlineData(5.0, 5.0, 10.0, 5.0, 5.2)]
    [InlineData(9.9, 10.0, 10.0, 9.8, 10.0)]
    [InlineData(-1.0, 20.0, 10.0, 0.0, 10.0)]
    [InlineData(4.0, 3.0, 10.0, 4.0, 4.2)]
    [InlineData(0.1, 0.0, 10.0, 0.1, 0.3)]
    public void NormalizeSelection_ClampsAndEnforcesMinimum(
        double inSeconds,
        double outSeconds,
        double upperSeconds,
        double expectedIn,
        double expectedOut)
    {
        var bounds = ClipSelectionLogic.NormalizeSelection(
            TimeSpan.FromSeconds(inSeconds),
            TimeSpan.FromSeconds(outSeconds),
            TimeSpan.FromSeconds(upperSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedIn), bounds.InPoint);
        Assert.Equal(TimeSpan.FromSeconds(expectedOut), bounds.OutPoint);
    }

    [Fact]
    public void NormalizeSelection_UpperBoundShorterThanMinimum_PinsToStart()
    {
        var bounds = ClipSelectionLogic.NormalizeSelection(
            TimeSpan.FromSeconds(0.05),
            TimeSpan.FromSeconds(0.05),
            TimeSpan.FromSeconds(0.1));

        Assert.Equal(TimeSpan.Zero, bounds.InPoint);
        Assert.Equal(TimeSpan.FromSeconds(0.1), bounds.OutPoint);
    }

    [Theory]
    [InlineData(8.0, 8.1, 10.0, 7.9)]
    [InlineData(-2.0, 5.0, 10.0, 0.0)]
    [InlineData(12.0, 10.0, 10.0, 9.8)]
    [InlineData(5.0, 5.2, 10.0, 5.0)]
    public void InPointFromPosition_ClampsToDurationAndKeepsMinimum(
        double positionSeconds,
        double outSeconds,
        double durationSeconds,
        double expectedIn)
    {
        var inPoint = ClipSelectionLogic.InPointFromPosition(
            TimeSpan.FromSeconds(positionSeconds),
            TimeSpan.FromSeconds(outSeconds),
            TimeSpan.FromSeconds(durationSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedIn), inPoint);
    }

    [Theory]
    [InlineData(0.0, 5.0, 10.0, 5.2)]
    [InlineData(12.0, 5.0, 10.0, 10.0)]
    [InlineData(-1.0, 5.0, 10.0, 5.2)]
    [InlineData(7.0, 5.0, 10.0, 7.0)]
    public void OutPointFromPosition_ClampsToDurationAndKeepsMinimum(
        double positionSeconds,
        double inSeconds,
        double durationSeconds,
        double expectedOut)
    {
        var outPoint = ClipSelectionLogic.OutPointFromPosition(
            TimeSpan.FromSeconds(positionSeconds),
            TimeSpan.FromSeconds(inSeconds),
            TimeSpan.FromSeconds(durationSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedOut), outPoint);
    }

    [Theory]
    [InlineData(true, true, 1.0)]
    [InlineData(true, false, 5.0)]
    [InlineData(false, true, -1.0)]
    [InlineData(false, false, -5.0)]
    public void SeekOffset_UsesFineAndCoarseSteps(bool forward, bool fine, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ClipSelectionLogic.SeekOffset(forward, fine));
    }

    [Theory]
    [InlineData(10.0, 9.9, false, true)]
    [InlineData(10.0, 9.8, false, true)]
    [InlineData(10.0, 9.79, false, false)]
    [InlineData(10.0, 9.9, true, false)]
    [InlineData(0.0, 5.0, false, false)]
    public void ShouldRestartFromStart_OnlyNearEndWhilePaused(
        double durationSeconds,
        double positionSeconds,
        bool isPlaying,
        bool expected)
    {
        var restart = ClipSelectionLogic.ShouldRestartFromStart(
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromSeconds(positionSeconds),
            isPlaying);

        Assert.Equal(expected, restart);
    }

    [Theory]
    [InlineData(5.0, 10.0, 0.0, 10.0)]
    [InlineData(25.0, 100.0, 15.0, 45.0)]
    [InlineData(3.0, 100.0, 0.0, 23.0)]
    public void HighlightWindow_ExpandsAroundPositionAndClamps(
        double positionSeconds,
        double durationSeconds,
        double expectedStart,
        double expectedEnd)
    {
        var window = ClipSelectionLogic.HighlightWindow(
            TimeSpan.FromSeconds(positionSeconds),
            TimeSpan.FromSeconds(durationSeconds),
            lead: TimeSpan.FromSeconds(10),
            tail: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(expectedStart), window.InPoint);
        Assert.Equal(TimeSpan.FromSeconds(expectedEnd), window.OutPoint);
    }

    [Fact]
    public void Constants_MatchTimelineRules()
    {
        Assert.Equal(0.2, ClipSelectionLogic.MinSelectionSeconds);
        Assert.Equal(1, ClipSelectionLogic.FineSeekSeconds);
        Assert.Equal(5, ClipSelectionLogic.CoarseSeekSeconds);
        Assert.Equal(0.2, ClipSelectionLogic.EndRestartToleranceSeconds);
        Assert.Equal(TimeSpan.FromSeconds(0.2), ClipSelectionLogic.MinSelection);
    }
}
