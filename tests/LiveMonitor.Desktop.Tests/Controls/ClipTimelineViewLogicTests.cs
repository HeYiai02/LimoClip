using System;
using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

/// <summary>
/// 时间轴控件纯逻辑（V0.7.5 抽离）的单元测试：缩放锚点与视口换算、缩放到选区、
/// 平移拖拽、手柄钳制、选区状态收敛、波形包络采样、刻度步长与时间文本。
/// </summary>
public sealed class ClipTimelineViewLogicTests
{
    private static readonly TimeSpan HundredSeconds = TimeSpan.FromSeconds(100);

    [Theory]
    [InlineData(1.0, 1.00005, false)]
    [InlineData(1.0, 1.0002, true)]
    [InlineData(1.0, 0.9999, false)]
    [InlineData(2.0, 2.01, true)]
    public void IsMeaningfulZoomChange_UsesEpsilon(double current, double next, bool expected) =>
        Assert.Equal(expected, ClipTimelineViewLogic.IsMeaningfulZoomChange(current, next));

    [Fact]
    public void ZoomAt_ClampsZoomToGeometryBounds()
    {
        var tooSmall = ClipTimelineViewLogic.ZoomAt(0.2, 0, 0.5, HundredSeconds);
        Assert.Equal(ClipTimelineGeometry.MinZoom, tooSmall.Zoom);

        var tooLarge = ClipTimelineViewLogic.ZoomAt(1000, 0, 0.5, HundredSeconds);
        Assert.Equal(ClipTimelineGeometry.MaxZoom, tooLarge.Zoom);
    }

    [Fact]
    public void ZoomAt_KeepsAnchorAtSameRelativePosition()
    {
        // 100s 时长、2x 缩放 => 可见 50s；锚点 50s 在中间 => 视口起点 25s。
        var viewport = ClipTimelineViewLogic.ZoomAt(2, 50, 0.5, HundredSeconds);
        Assert.Equal(2, viewport.Zoom);
        Assert.Equal(25, viewport.ViewStartSeconds, 6);
    }

    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(100, 0, 50)]
    [InlineData(100, 1, 50)]
    [InlineData(0, 1, 0)]
    [InlineData(60, 1, 10)]
    public void ZoomAt_ClampsViewStartIntoRange(double anchorSeconds, double anchorRatio, double expectedStart)
    {
        var viewport = ClipTimelineViewLogic.ZoomAt(2, anchorSeconds, anchorRatio, HundredSeconds);
        Assert.Equal(2, viewport.Zoom);
        Assert.Equal(expectedStart, viewport.ViewStartSeconds, 6);
    }

    [Fact]
    public void ZoomAt_WithZeroDuration_ProducesZeroViewStart()
    {
        var viewport = ClipTimelineViewLogic.ZoomAt(4, 30, 0.5, TimeSpan.Zero);
        Assert.Equal(4, viewport.Zoom);
        Assert.Equal(0, viewport.ViewStartSeconds);
    }

    [Fact]
    public void ZoomToCenter_UsesMiddleRatio()
    {
        var viewport = ClipTimelineViewLogic.ZoomToCenter(2, 50, HundredSeconds);
        Assert.Equal(25, viewport.ViewStartSeconds, 6);
    }

    [Fact]
    public void TryFitRange_WithUnknownDuration_Fails()
    {
        Assert.False(ClipTimelineViewLogic.TryFitRange(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), out var viewport));
        Assert.Equal(ClipTimelineGeometry.MinZoom, viewport.Zoom);
        Assert.Equal(0, viewport.ViewStartSeconds);
    }

    [Fact]
    public void TryFitRange_ExactRangeFillsViewport()
    {
        Assert.True(ClipTimelineViewLogic.TryFitRange(
            HundredSeconds,
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(60),
            out var viewport));
        Assert.Equal(5, viewport.Zoom, 6);
        Assert.Equal(40, viewport.ViewStartSeconds, 6);
    }

    [Fact]
    public void TryFitRange_EnforcesMinimumSpan()
    {
        // 0.2s 选区按最小 0.5s 处理：100/0.5=200x 被夹到 60x，可见 1.6667s，居中后起点 ~9.4167s。
        Assert.True(ClipTimelineViewLogic.TryFitRange(
            HundredSeconds,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10.2),
            out var viewport));
        Assert.Equal(ClipTimelineGeometry.MaxZoom, viewport.Zoom);
        Assert.Equal(10 - (100.0 / 60 - 0.5) / 2, viewport.ViewStartSeconds, 6);
    }

    [Fact]
    public void TryFitRange_ClampsAtTail()
    {
        Assert.True(ClipTimelineViewLogic.TryFitRange(
            HundredSeconds,
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(100),
            out var viewport));
        Assert.Equal(10, viewport.Zoom, 6);
        Assert.Equal(90, viewport.ViewStartSeconds, 6);
    }

    [Fact]
    public void PanViewStart_WithZeroPlotWidth_KeepsStart()
    {
        Assert.Equal(5, ClipTimelineViewLogic.PanViewStart(5, 100, 0, TimeSpan.FromSeconds(50), HundredSeconds, 2));
    }

    [Fact]
    public void PanViewStart_ConvertsPixelsToSeconds()
    {
        // 可见 50s、绘图宽 1000px：右拖 100px => 起点左移 5s。
        Assert.Equal(15, ClipTimelineViewLogic.PanViewStart(20, 100, 1000, TimeSpan.FromSeconds(50), HundredSeconds, 2), 6);
        Assert.Equal(25, ClipTimelineViewLogic.PanViewStart(20, -100, 1000, TimeSpan.FromSeconds(50), HundredSeconds, 2), 6);
    }

    [Fact]
    public void PanViewStart_ClampsAtBothEnds()
    {
        Assert.Equal(0, ClipTimelineViewLogic.PanViewStart(20, 100000, 1000, TimeSpan.FromSeconds(50), HundredSeconds, 2), 6);
        Assert.Equal(50, ClipTimelineViewLogic.PanViewStart(20, -100000, 1000, TimeSpan.FromSeconds(50), HundredSeconds, 2), 6);
    }

    [Fact]
    public void DisplayedCenterSeconds_AddsHalfVisibleDuration() =>
        Assert.Equal(50, ClipTimelineViewLogic.DisplayedCenterSeconds(25, TimeSpan.FromSeconds(50)), 6);

    [Theory]
    [InlineData(10, 20, 0, 10)]
    [InlineData(25, 20, 0, 19.8)]
    [InlineData(-5, 20, 0, 0)]
    [InlineData(30, 100, 5, 5)]
    public void ClampDragInPoint_Works(double candidate, double outPointSeconds, double safeSeconds, double expected)
    {
        var result = ClipTimelineViewLogic.ClampDragInPoint(
            candidate,
            TimeSpan.FromSeconds(outPointSeconds),
            TimeSpan.FromSeconds(safeSeconds));
        Assert.Equal(expected, result, 6);
    }

    [Theory]
    [InlineData(30, 10, 100, 0, 30)]
    [InlineData(5, 10, 100, 0, 10.2)]
    [InlineData(200, 10, 100, 0, 100)]
    [InlineData(80, 10, 100, 50, 50)]
    [InlineData(10, 30, 100, 20, 30.2)]
    public void ClampDragOutPoint_Works(double candidate, double inPointSeconds, double durationSeconds, double safeSeconds, double expected)
    {
        var result = ClipTimelineViewLogic.ClampDragOutPoint(
            candidate,
            TimeSpan.FromSeconds(inPointSeconds),
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromSeconds(safeSeconds));
        Assert.Equal(expected, result, 6);
    }

    [Fact]
    public void ClampState_WithUnknownDuration_ResetsEverything()
    {
        var state = ClipTimelineViewLogic.ClampState(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(3),
            5,
            10);
        Assert.Equal(TimeSpan.Zero, state.InPoint);
        Assert.Equal(TimeSpan.Zero, state.OutPoint);
        Assert.Equal(TimeSpan.Zero, state.Position);
        Assert.Equal(ClipTimelineGeometry.MinZoom, state.Zoom);
        Assert.Equal(0, state.ViewStartSeconds);
    }

    [Fact]
    public void ClampState_FixesOutOfRangeSelectionAndView()
    {
        var state = ClipTimelineViewLogic.ClampState(
            HundredSeconds,
            TimeSpan.FromSeconds(-5),
            TimeSpan.FromSeconds(200),
            TimeSpan.FromSeconds(300),
            5,
            100);
        Assert.Equal(TimeSpan.Zero, state.InPoint);
        Assert.Equal(HundredSeconds, state.OutPoint);
        Assert.Equal(HundredSeconds, state.Position);
        Assert.Equal(5, state.Zoom);
        // 5x => 可见 20s => 起点上限 80s。
        Assert.Equal(80, state.ViewStartSeconds, 6);
    }

    [Fact]
    public void ClampState_ExtendsSelectionForwardWhenRoom()
    {
        var state = ClipTimelineViewLogic.ClampState(
            HundredSeconds,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10.05),
            TimeSpan.FromSeconds(10),
            1,
            0);
        Assert.Equal(TimeSpan.FromSeconds(10), state.InPoint);
        Assert.Equal(TimeSpan.FromSeconds(10.2), state.OutPoint);
    }

    [Fact]
    public void ClampState_BacksOffSelectionAtTail()
    {
        var state = ClipTimelineViewLogic.ClampState(
            HundredSeconds,
            TimeSpan.FromSeconds(99.95),
            HundredSeconds,
            HundredSeconds,
            1,
            0);
        Assert.Equal(TimeSpan.FromSeconds(99.8), state.InPoint);
        Assert.Equal(HundredSeconds, state.OutPoint);
    }

    [Fact]
    public void ClampState_ClampsPositionIntoDuration()
    {
        var state = ClipTimelineViewLogic.ClampState(
            HundredSeconds,
            TimeSpan.Zero,
            HundredSeconds,
            TimeSpan.FromSeconds(-10),
            1,
            0);
        Assert.Equal(TimeSpan.Zero, state.Position);

        state = ClipTimelineViewLogic.ClampState(
            HundredSeconds,
            TimeSpan.Zero,
            HundredSeconds,
            TimeSpan.FromSeconds(500),
            1,
            0);
        Assert.Equal(HundredSeconds, state.Position);
    }

    [Fact]
    public void SampleEnvelope_HandlesEmptyAndInvalidInput()
    {
        Assert.Equal(0, ClipTimelineViewLogic.SampleEnvelope(Array.Empty<float>(), 1, 1));
        Assert.Equal(0, ClipTimelineViewLogic.SampleEnvelope(new[] { 0.1f }, 1, 0));
        Assert.Equal(0, ClipTimelineViewLogic.SampleEnvelope(new[] { 0.1f }, 1, -0.5));
    }

    [Theory]
    [InlineData(-1, 0.2)]
    [InlineData(0, 0.2)]
    [InlineData(10, 0.6)]
    [InlineData(0.5, 0.3)]
    [InlineData(1.25, 0.45)]
    public void SampleEnvelope_InterpolatesAndClamps(double seconds, double expected) =>
        Assert.Equal(expected, ClipTimelineViewLogic.SampleEnvelope(new[] { 0.2f, 0.4f, 0.6f }, seconds, 1), 6);

    [Fact]
    public void ChooseRulerStep_DefaultsToFirstStepWithoutWidth() =>
        Assert.Equal(1, ClipTimelineViewLogic.ChooseRulerStep(100, 0));

    [Theory]
    [InlineData(100, 800, 15)]
    [InlineData(10, 800, 2)]
    [InlineData(14400, 800, 1800)]
    [InlineData(100000, 800, 14400)]
    public void ChooseRulerStep_KeepsLabelsReadable(double visibleSeconds, double widthPx, double expected) =>
        Assert.Equal(expected, ClipTimelineViewLogic.ChooseRulerStep(visibleSeconds, widthPx));

    [Theory]
    [InlineData(59.9, false, "0:59")]
    [InlineData(60, false, "1:00")]
    [InlineData(3661, false, "61:01")]
    [InlineData(3661, true, "1:01:01")]
    [InlineData(-5, false, "0:00")]
    [InlineData(0, true, "0:00:00")]
    public void FormatRulerTime_Works(double seconds, bool withHours, string expected) =>
        Assert.Equal(expected, ClipTimelineViewLogic.FormatRulerTime(seconds, withHours));
}
