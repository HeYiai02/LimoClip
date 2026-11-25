using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class HealthSummaryCoordinatorTests
{
    private const string PaneA = "pane-1";
    private const string PaneB = "pane-2";

    private static PaneHealthSample Sample(string paneId, long droppedFrames, double framesPerSecond)
        => new(paneId, droppedFrames, framesPerSecond);

    [Fact]
    public void FirstObservation_OnlyEstablishesBaseline()
    {
        var coordinator = new HealthSummaryCoordinator();

        var summary = coordinator.Update(new[] { Sample(PaneA, 100, 30) });

        Assert.Equal(0, summary.DroppedFrames);
        Assert.Equal(0, summary.ExpectedFrames);
        Assert.Equal(0, summary.DropPercent);
        Assert.False(summary.IsWarning);
        Assert.Equal("解码正常 (0% 丢帧)", summary.Text);
    }

    [Fact]
    public void SecondObservation_ReportsDeltaAndWarningAboveThreshold()
    {
        var coordinator = new HealthSummaryCoordinator();
        coordinator.Update(new[] { Sample(PaneA, 100, 30) });

        var summary = coordinator.Update(new[] { Sample(PaneA, 110, 30) });

        Assert.Equal(10, summary.DroppedFrames);
        Assert.Equal(60, summary.ExpectedFrames);
        Assert.Equal(100.0 / 6.0, summary.DropPercent, 9);
        Assert.True(summary.IsWarning);
        Assert.StartsWith("解码告警 (", summary.Text);
        Assert.EndsWith("% 丢帧)", summary.Text);
    }

    [Fact]
    public void NegativeDelta_ClearsToZeroAndZeroFpsUsesMinimumRate()
    {
        var coordinator = new HealthSummaryCoordinator();
        coordinator.Update(new[] { Sample(PaneA, 100, 0) });

        var normal = coordinator.Update(new[] { Sample(PaneA, 90, 0) });
        var warning = coordinator.Update(new[] { Sample(PaneA, 91, 0) });

        Assert.Equal(0, normal.DroppedFrames);
        Assert.Equal(2, normal.ExpectedFrames);
        Assert.False(normal.IsWarning);
        Assert.Equal(1, warning.DroppedFrames);
        Assert.Equal(2, warning.ExpectedFrames);
        Assert.Equal(50.0, warning.DropPercent);
        Assert.True(warning.IsWarning);
    }

    [Fact]
    public void PaneLeavingView_RemovesStateAndResetsBaselineOnReturn()
    {
        var coordinator = new HealthSummaryCoordinator();
        coordinator.Update(new[] { Sample(PaneA, 0, 30), Sample(PaneB, 0, 30) });

        var bothSeen = coordinator.Update(new[] { Sample(PaneA, 10, 30), Sample(PaneB, 0, 30) });
        Assert.Equal(10, bothSeen.DroppedFrames);
        Assert.Equal(120, bothSeen.ExpectedFrames);
        Assert.True(bothSeen.IsWarning);

        var paneBGone = coordinator.Update(new[] { Sample(PaneA, 10, 30) });
        Assert.Equal(0, paneBGone.DroppedFrames);
        Assert.Equal(60, paneBGone.ExpectedFrames);

        var paneBBack = coordinator.Update(new[] { Sample(PaneA, 10, 30), Sample(PaneB, 5, 30) });
        Assert.Equal(0, paneBBack.DroppedFrames);
        Assert.Equal(60, paneBBack.ExpectedFrames);

        var paneBGrew = coordinator.Update(new[] { Sample(PaneA, 10, 30), Sample(PaneB, 7, 30) });
        Assert.Equal(2, paneBGrew.DroppedFrames);
        Assert.Equal(120, paneBGrew.ExpectedFrames);
        Assert.False(paneBGrew.IsWarning);
        Assert.StartsWith("解码正常 (", paneBGrew.Text);
    }

    [Fact]
    public void ThresholdAtFivePercent_IsWarning()
    {
        var coordinator = new HealthSummaryCoordinator();
        coordinator.Update(new[] { Sample(PaneA, 0, 30) });

        var summary = coordinator.Update(new[] { Sample(PaneA, 3, 30) });

        Assert.Equal(5.0, summary.DropPercent);
        Assert.True(summary.IsWarning);
    }

    [Fact]
    public void ZeroFps_UsesMinimumExpectedFrames()
    {
        var coordinator = new HealthSummaryCoordinator();
        coordinator.Update(new[] { Sample(PaneA, 0, 0) });

        var summary = coordinator.Update(new[] { Sample(PaneA, 1, 0) });

        Assert.Equal(2, summary.ExpectedFrames);
        Assert.Equal(50.0, summary.DropPercent);
        Assert.True(summary.IsWarning);
    }
}
