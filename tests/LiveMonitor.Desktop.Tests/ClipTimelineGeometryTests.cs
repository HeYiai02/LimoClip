using System;
using System.Collections.Generic;
using Avalonia;
using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// <see cref="ClipTimelineGeometry"/> 的纯逻辑测试：行带布局、缩放/平移钳制、安全时长钳制、
/// 时间与控件 X 坐标互转、高能标记与入/出点手柄的命中优先级。
/// <para>
/// 基准布局取 <c>ComputeLayout(800, 200)</c>（Plot=(10,8,780,184)，高能行 y=154.75..192），
/// 基准视口取 100s 全片可见（1x），此时 X = 10 + 7.8 * 秒。
/// </para>
/// </summary>
public sealed class ClipTimelineGeometryTests
{
    private static readonly TimeSpan FullDuration = TimeSpan.FromSeconds(100);

    private static readonly TimeSpan FullVisible = TimeSpan.FromSeconds(100);

    private static TimelineLayout Layout(double width = 800, double height = 200) =>
        ClipTimelineGeometry.ComputeLayout(width, height);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ClipHighlightMarker Marker(double seconds, string? reason = null) =>
        new(TimeSpan.FromSeconds(seconds), reason);

    private static TimelineHitTarget HitTest(
        double x,
        double y,
        out ClipHighlightMarker? marker,
        TimeSpan? inPoint = null,
        TimeSpan? outPoint = null,
        TimeSpan? position = null,
        IReadOnlyList<ClipHighlightMarker>? highlights = null,
        double viewStartSeconds = 0,
        TimeSpan? visibleDuration = null,
        TimeSpan? duration = null,
        TimelineLayout? layout = null)
    {
        var resolvedDuration = duration ?? FullDuration;
        var resolvedVisible = visibleDuration ?? ClipTimelineGeometry.GetVisibleDuration(resolvedDuration, 1.0);
        return ClipTimelineGeometry.HitTest(
            x,
            y,
            layout ?? Layout(),
            viewStartSeconds,
            resolvedVisible,
            inPoint ?? Seconds(20),
            outPoint ?? Seconds(80),
            position ?? Seconds(30),
            highlights,
            out marker);
    }

    // ---------------------------------------------------------------- 布局

    [Fact]
    public void ComputeLayout_normal_bounds_produces_expected_bands()
    {
        var layout = Layout();

        Assert.Equal(new Rect(10, 8, 780, 184), layout.Plot);
        Assert.Equal(new Rect(10, 8, 780, 20), layout.Ruler);
        Assert.Equal(new Rect(10, 33, 780, 74.5), layout.Wave);
        Assert.Equal(new Rect(10, 112.5, 780, 37.25), layout.Density);
        Assert.Equal(new Rect(10, 154.75, 780, 37.25), layout.Highlight);
    }

    [Fact]
    public void ComputeLayout_small_bounds_keeps_minimum_row_heights()
    {
        var layout = Layout(320, 60);

        Assert.Equal(new Rect(10, 8, 300, 44), layout.Plot);
        Assert.Equal(12.0, layout.Wave.Height);
        Assert.Equal(6.0, layout.Density.Height);
        Assert.Equal(10.0, layout.Highlight.Height);
        Assert.Equal(28.0, layout.Wave.Height + layout.Density.Height + layout.Highlight.Height);
    }

    [Fact]
    public void ComputeLayout_tiny_bounds_keeps_plot_positive()
    {
        var layout = Layout(0, 0);

        Assert.Equal(new Rect(10, 8, 1, 24), layout.Plot);
        Assert.Equal(1.0, layout.Ruler.Width);
        Assert.True(layout.Wave.Height > 0);
        Assert.True(layout.Highlight.Height >= 10);
    }

    // ---------------------------------------------------------------- 缩放

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(12.5, 12.5)]
    [InlineData(60.0, 60.0)]
    [InlineData(120.0, 60.0)]
    [InlineData(-3.0, 1.0)]
    public void ClampZoom_clamps_to_supported_range(double input, double expected)
    {
        Assert.Equal(expected, ClipTimelineGeometry.ClampZoom(input));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void GetVisibleDuration_returns_full_duration_at_min_zoom(double zoom)
    {
        Assert.Equal(FullDuration, ClipTimelineGeometry.GetVisibleDuration(FullDuration, zoom));
    }

    [Theory]
    [InlineData(10.0, 10.0)]
    [InlineData(60.0, 100.0 / 60.0)]
    public void GetVisibleDuration_divides_duration_by_zoom(double zoom, double expectedSeconds)
    {
        var visible = ClipTimelineGeometry.GetVisibleDuration(FullDuration, zoom);

        Assert.Equal(expectedSeconds, visible.TotalSeconds, 6);
    }

    [Fact]
    public void GetVisibleDuration_zero_duration_returns_zero()
    {
        Assert.Equal(TimeSpan.Zero, ClipTimelineGeometry.GetVisibleDuration(TimeSpan.Zero, 10));
    }

    // ---------------------------------------------------------------- 平移

    [Theory]
    [InlineData(1.0, 50.0)]
    [InlineData(0.5, 50.0)]
    public void ClampViewStart_full_view_forces_zero(double zoom, double startSeconds)
    {
        Assert.Equal(0.0, ClipTimelineGeometry.ClampViewStart(startSeconds, FullDuration, zoom));
    }

    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(40.0, 40.0)]
    [InlineData(90.0, 90.0)]
    [InlineData(120.0, 90.0)]
    public void ClampViewStart_clamps_to_visible_window_at_10x(double startSeconds, double expected)
    {
        Assert.Equal(expected, ClipTimelineGeometry.ClampViewStart(startSeconds, FullDuration, 10));
    }

    [Fact]
    public void ClampViewStart_recomputes_limit_after_duration_change()
    {
        // 10x 下 100s 视频的最大起点是 90s；时长变为 50s 后最大起点是 45s。
        Assert.Equal(45.0, ClipTimelineGeometry.ClampViewStart(60, TimeSpan.FromSeconds(50), 10));
        Assert.Equal(40.0, ClipTimelineGeometry.ClampViewStart(40, TimeSpan.FromSeconds(50), 10));
    }

    [Fact]
    public void ClampViewStart_zero_duration_returns_zero()
    {
        Assert.Equal(0.0, ClipTimelineGeometry.ClampViewStart(30, TimeSpan.Zero, 10));
    }

    // ---------------------------------------------------------------- 时间钳制

    [Theory]
    [InlineData(30.0, 10.0, 10.0)]
    [InlineData(10.0, 10.0, 10.0)]
    [InlineData(4.0, 10.0, 4.0)]
    [InlineData(0.0, 10.0, 0.0)]
    public void ClampTime_limits_to_video_duration(double timeSeconds, double durationSeconds, double expectedSeconds)
    {
        var clamped = ClipTimelineGeometry.ClampTime(Seconds(timeSeconds), Seconds(durationSeconds));

        Assert.Equal(expectedSeconds, clamped.TotalSeconds, 6);
    }

    [Fact]
    public void ClampTime_zero_duration_keeps_value()
    {
        Assert.Equal(Seconds(5), ClipTimelineGeometry.ClampTime(Seconds(5), TimeSpan.Zero));
    }

    [Fact]
    public void ClampTime_negative_time_returns_zero()
    {
        Assert.Equal(TimeSpan.Zero, ClipTimelineGeometry.ClampTime(Seconds(-2), FullDuration));
    }

    [Fact]
    public void ClampTime_limits_realtime_safe_duration_to_current_duration()
    {
        // ClipTimelineControl.SafeDuration setter 走 ClampTime：录制中时长变化时把安全截止时间压回片长。
        Assert.Equal(Seconds(30), ClipTimelineGeometry.ClampTime(Seconds(45), Seconds(30)));
        Assert.Equal(Seconds(20), ClipTimelineGeometry.ClampTime(Seconds(20), Seconds(30)));
    }

    // ---------------------------------------------------------------- 边录边剪安全时长

    [Theory]
    [InlineData(0.0, 30.0, 0.0)]
    [InlineData(12.0, 30.0, 12.0)]
    [InlineData(30.0, 30.0, 30.0)]
    public void ClampToSafe_allows_value_within_safe_duration(double timeSeconds, double safeSeconds, double expectedSeconds)
    {
        var clamped = ClipTimelineGeometry.ClampToSafe(Seconds(timeSeconds), Seconds(safeSeconds));

        Assert.Equal(expectedSeconds, clamped.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(45.0, 30.0, 30.0)]
    [InlineData(30.1, 30.0, 30.0)]
    public void ClampToSafe_clamps_value_beyond_safe_duration(double timeSeconds, double safeSeconds, double expectedSeconds)
    {
        var clamped = ClipTimelineGeometry.ClampToSafe(Seconds(timeSeconds), Seconds(safeSeconds));

        Assert.Equal(expectedSeconds, clamped.TotalSeconds, 6);
    }

    [Fact]
    public void ClampToSafe_is_disabled_when_safe_duration_not_enabled()
    {
        Assert.Equal(Seconds(45), ClipTimelineGeometry.ClampToSafe(Seconds(45), TimeSpan.Zero));
        Assert.Equal(FullDuration, ClipTimelineGeometry.ClampToSafe(FullDuration, TimeSpan.Zero));
    }

    [Fact]
    public void ClampToSafe_tightens_after_safe_duration_shrinks()
    {
        var position = ClipTimelineGeometry.ClampToSafe(Seconds(25), Seconds(30));
        Assert.Equal(Seconds(25), position);

        position = ClipTimelineGeometry.ClampToSafe(position, Seconds(20));
        Assert.Equal(Seconds(20), position);
    }

    // ---------------------------------------------------------------- 坐标/时间换算

    [Theory]
    [InlineData(10.0, 0.0)]
    [InlineData(400.0, 50.0)]
    [InlineData(790.0, 100.0)]
    public void XToTime_maps_plot_edges_to_duration(double x, double expectedSeconds)
    {
        var time = ClipTimelineGeometry.XToTime(x, Layout(), 0, FullVisible, FullDuration);

        Assert.Equal(expectedSeconds, time.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(-50.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(1000.0, 100.0)]
    public void XToTime_clamps_outside_plot(double x, double expectedSeconds)
    {
        var time = ClipTimelineGeometry.XToTime(x, Layout(), 0, FullVisible, FullDuration);

        Assert.Equal(expectedSeconds, time.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(0.0, 10.0)]
    [InlineData(50.0, 400.0)]
    [InlineData(100.0, 790.0)]
    public void TimeToX_maps_time_to_plot_edges(double seconds, double expectedX)
    {
        var x = ClipTimelineGeometry.TimeToX(Seconds(seconds), Layout(), 0, FullVisible);

        Assert.Equal(expectedX, x, 6);
    }

    [Fact]
    public void XToTime_respects_zoomed_viewport()
    {
        // 10x 缩放、视口从 20s 开始：内容区左/中/右分别对应 20s/25s/30s。
        var visible = Seconds(10);

        Assert.Equal(20.0, ClipTimelineGeometry.XToTime(10, Layout(), 20, visible, FullDuration).TotalSeconds, 6);
        Assert.Equal(25.0, ClipTimelineGeometry.XToTime(400, Layout(), 20, visible, FullDuration).TotalSeconds, 6);
        Assert.Equal(30.0, ClipTimelineGeometry.XToTime(790, Layout(), 20, visible, FullDuration).TotalSeconds, 6);
    }

    [Fact]
    public void TimeToX_respects_zoomed_viewport()
    {
        var visible = Seconds(10);

        Assert.Equal(10.0, ClipTimelineGeometry.TimeToX(Seconds(20), Layout(), 20, visible), 6);
        Assert.Equal(400.0, ClipTimelineGeometry.TimeToX(Seconds(25), Layout(), 20, visible), 6);
        Assert.Equal(790.0, ClipTimelineGeometry.TimeToX(Seconds(30), Layout(), 20, visible), 6);
    }

    [Fact]
    public void TimeToX_zero_visible_duration_falls_back_to_plot_left()
    {
        Assert.Equal(10.0, ClipTimelineGeometry.TimeToX(Seconds(5), Layout(), 0, TimeSpan.Zero));
    }

    [Fact]
    public void XToTime_zero_width_plot_returns_zero()
    {
        var layout = new TimelineLayout(new Rect(10, 8, 0, 184), new Rect(10, 8, 0, 20), new Rect(), new Rect(), new Rect());

        Assert.Equal(TimeSpan.Zero, ClipTimelineGeometry.XToTime(50, layout, 0, FullVisible, FullDuration));
    }

    [Fact]
    public void TimeToX_zero_width_plot_returns_plot_left()
    {
        var layout = new TimelineLayout(new Rect(10, 8, 0, 184), new Rect(10, 8, 0, 20), new Rect(), new Rect(), new Rect());

        Assert.Equal(10.0, ClipTimelineGeometry.TimeToX(Seconds(50), layout, 0, FullVisible));
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(166.0)]
    [InlineData(244.0)]
    [InlineData(400.0)]
    [InlineData(634.0)]
    [InlineData(790.0)]
    public void XToTime_and_TimeToX_roundtrip(double x)
    {
        var layout = Layout();
        var time = ClipTimelineGeometry.XToTime(x, layout, 0, FullVisible, FullDuration);
        var roundTrip = ClipTimelineGeometry.TimeToX(time, layout, 0, FullVisible);

        Assert.Equal(x, roundTrip, 4);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(100.0)]
    [InlineData(400.0)]
    [InlineData(700.0)]
    [InlineData(790.0)]
    public void XToTime_and_TimeToX_roundtrip_in_zoomed_viewport(double x)
    {
        var layout = Layout();
        var visible = Seconds(10);
        var time = ClipTimelineGeometry.XToTime(x, layout, 20, visible, FullDuration);
        var roundTrip = ClipTimelineGeometry.TimeToX(time, layout, 20, visible);

        Assert.Equal(x, roundTrip, 4);
    }

    // ---------------------------------------------------------------- 高能标记命中

    [Fact]
    public void TryHitHighlight_returns_false_for_null_or_empty_list()
    {
        var layout = Layout();

        Assert.False(ClipTimelineGeometry.TryHitHighlight(400, 170, layout, 0, FullVisible, null, out var nullMarker));
        Assert.Null(nullMarker);

        Assert.False(ClipTimelineGeometry.TryHitHighlight(
            400, 170, layout, 0, FullVisible, Array.Empty<ClipHighlightMarker>(), out var emptyMarker));
        Assert.Null(emptyMarker);
    }

    [Fact]
    public void TryHitHighlight_returns_marker_under_pointer()
    {
        var layout = Layout();
        var markers = new[] { Marker(20), Marker(50), Marker(80) };

        var hit = ClipTimelineGeometry.TryHitHighlight(400, 170, layout, 0, FullVisible, markers, out var marker);

        Assert.True(hit);
        Assert.Same(markers[1], marker);
    }

    [Fact]
    public void TryHitHighlight_returns_first_matching_marker()
    {
        // 50.5s 处标记的 X 约为 403.9，与 x=402 的距离小于 8px，先于 50s 标记出现时优先命中。
        var layout = Layout();
        var markers = new[] { Marker(50.5), Marker(50) };

        var hit = ClipTimelineGeometry.TryHitHighlight(402, 170, layout, 0, FullVisible, markers, out var marker);

        Assert.True(hit);
        Assert.Same(markers[0], marker);
    }

    [Theory]
    [InlineData(392.0)]
    [InlineData(408.0)]
    public void TryHitHighlight_accepts_horizontal_tolerance_boundaries(double x)
    {
        var layout = Layout();

        Assert.True(ClipTimelineGeometry.TryHitHighlight(x, 170, layout, 0, FullVisible, new[] { Marker(50) }, out _));
    }

    [Theory]
    [InlineData(391.5)]
    [InlineData(408.5)]
    public void TryHitHighlight_rejects_pointer_beyond_horizontal_tolerance(double x)
    {
        var layout = Layout();

        Assert.False(ClipTimelineGeometry.TryHitHighlight(x, 170, layout, 0, FullVisible, new[] { Marker(50) }, out _));
    }

    [Theory]
    [InlineData(150.75)]
    [InlineData(196.0)]
    public void TryHitHighlight_accepts_vertical_tolerance_boundaries(double y)
    {
        var layout = Layout();

        Assert.True(ClipTimelineGeometry.TryHitHighlight(400, y, layout, 0, FullVisible, new[] { Marker(50) }, out _));
    }

    [Theory]
    [InlineData(150.5)]
    [InlineData(196.5)]
    public void TryHitHighlight_rejects_pointer_outside_highlight_band(double y)
    {
        var layout = Layout();

        Assert.False(ClipTimelineGeometry.TryHitHighlight(400, y, layout, 0, FullVisible, new[] { Marker(50) }, out _));
    }

    [Fact]
    public void TryHitHighlight_measures_position_in_current_viewport()
    {
        // 10x 缩放、从 20s 开始：25s 标记落在 x=400；全片可见时 25s 标记在 x=205。
        var layout = Layout();
        var visible = Seconds(10);

        Assert.True(ClipTimelineGeometry.TryHitHighlight(400, 170, layout, 20, visible, new[] { Marker(25) }, out _));
        Assert.False(ClipTimelineGeometry.TryHitHighlight(205, 170, layout, 20, visible, new[] { Marker(25) }, out _));
    }

    // ---------------------------------------------------------------- 命中优先级

    [Fact]
    public void HitTest_click_on_highlight_returns_highlight()
    {
        var marker = Marker(50);

        var hit = HitTest(400, 170, out var hitMarker, highlights: new[] { marker });

        Assert.Equal(TimelineHitTarget.Highlight, hit);
        Assert.Same(marker, hitMarker);
    }

    [Fact]
    public void HitTest_in_handle_beats_highlight_at_same_x()
    {
        var marker = Marker(20);

        var hit = HitTest(166, 170, out var hitMarker, highlights: new[] { marker });

        Assert.Equal(TimelineHitTarget.InHandle, hit);
        Assert.Null(hitMarker);
    }

    [Fact]
    public void HitTest_out_handle_beats_highlight_at_same_x()
    {
        var marker = Marker(80);

        var hit = HitTest(634, 170, out var hitMarker, highlights: new[] { marker });

        Assert.Equal(TimelineHitTarget.OutHandle, hit);
        Assert.Null(hitMarker);
    }

    [Fact]
    public void HitTest_in_handle_beats_out_handle_when_both_in_range()
    {
        // 入点 50s（x=400）、出点 50.5s（x≈403.9），x=402 同时落在两个手柄的 7px 容差内。
        var hit = HitTest(402, 170, out _, inPoint: Seconds(50), outPoint: Seconds(50.5), position: Seconds(30));

        Assert.Equal(TimelineHitTarget.InHandle, hit);
    }

    [Fact]
    public void HitTest_highlight_beats_playhead_at_same_x()
    {
        var marker = Marker(50);

        var hit = HitTest(400, 170, out var hitMarker, position: Seconds(50), highlights: new[] { marker });

        Assert.Equal(TimelineHitTarget.Highlight, hit);
        Assert.Same(marker, hitMarker);
    }

    [Fact]
    public void HitTest_returns_playhead_when_only_playhead_is_near()
    {
        var hit = HitTest(244, 170, out var hitMarker, position: Seconds(30));

        Assert.Equal(TimelineHitTarget.Playhead, hit);
        Assert.Null(hitMarker);
    }

    [Fact]
    public void HitTest_returns_none_when_nothing_is_hit()
    {
        var hit = HitTest(300, 170, out var hitMarker, highlights: new[] { Marker(50) });

        Assert.Equal(TimelineHitTarget.None, hit);
        Assert.Null(hitMarker);
    }

    [Fact]
    public void HitTest_returns_none_over_highlight_band_without_markers()
    {
        Assert.Equal(TimelineHitTarget.None, HitTest(400, 170, out _));

        var hit = HitTest(400, 170, out _, highlights: Array.Empty<ClipHighlightMarker>());

        Assert.Equal(TimelineHitTarget.None, hit);
    }

    [Theory]
    [InlineData(159.0, TimelineHitTarget.InHandle)]
    [InlineData(166.0, TimelineHitTarget.InHandle)]
    [InlineData(173.0, TimelineHitTarget.InHandle)]
    [InlineData(158.5, TimelineHitTarget.None)]
    [InlineData(173.5, TimelineHitTarget.None)]
    public void HitTest_handle_horizontal_tolerance_is_inclusive(double x, TimelineHitTarget expected)
    {
        Assert.Equal(expected, HitTest(x, 170, out _));
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(300.0)]
    public void HitTest_handles_ignore_vertical_position(double y)
    {
        Assert.Equal(TimelineHitTarget.InHandle, HitTest(166, y, out _));
    }

    [Theory]
    [InlineData(392.0, TimelineHitTarget.Highlight)]
    [InlineData(408.0, TimelineHitTarget.Highlight)]
    [InlineData(391.5, TimelineHitTarget.None)]
    [InlineData(408.5, TimelineHitTarget.None)]
    public void HitTest_highlight_horizontal_tolerance_is_inclusive(double x, TimelineHitTarget expected)
    {
        var hit = HitTest(x, 170, out _, highlights: new[] { Marker(50) });

        Assert.Equal(expected, hit);
    }

    [Theory]
    [InlineData(150.75, TimelineHitTarget.Highlight)]
    [InlineData(196.0, TimelineHitTarget.Highlight)]
    [InlineData(150.5, TimelineHitTarget.None)]
    [InlineData(196.5, TimelineHitTarget.None)]
    public void HitTest_highlight_vertical_tolerance_is_inclusive(double y, TimelineHitTarget expected)
    {
        var hit = HitTest(400, y, out _, highlights: new[] { Marker(50) });

        Assert.Equal(expected, hit);
    }

    [Theory]
    [InlineData(238.0, TimelineHitTarget.Playhead)]
    [InlineData(250.0, TimelineHitTarget.Playhead)]
    [InlineData(237.5, TimelineHitTarget.None)]
    [InlineData(250.5, TimelineHitTarget.None)]
    public void HitTest_playhead_horizontal_tolerance_is_inclusive(double x, TimelineHitTarget expected)
    {
        Assert.Equal(expected, HitTest(x, 170, out _, position: Seconds(30)));
    }

    [Fact]
    public void HitTest_measures_handles_in_current_viewport()
    {
        // 10x 缩放、从 20s 开始：入点 22s → x=166，出点 28s → x=634。
        var layout = Layout();
        var visible = Seconds(10);
        var hit = ClipTimelineGeometry.HitTest(
            166,
            170,
            layout,
            20,
            visible,
            Seconds(22),
            Seconds(28),
            Seconds(25),
            null,
            out _);

        Assert.Equal(TimelineHitTarget.InHandle, hit);
    }
}
