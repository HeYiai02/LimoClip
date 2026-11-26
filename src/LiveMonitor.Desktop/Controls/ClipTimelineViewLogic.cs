using System;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 时间轴控件（<see cref="ClipTimelineControl"/>）中与 Avalonia 无关的纯逻辑：
/// 缩放/视口换算、拖拽钳制、选区状态收敛、波形包络采样、刻度步长与时间文本。
/// 这些逻辑原先内嵌在控件中，抽离后可在不创建控件的情况下直接单测。
/// </summary>
internal static class ClipTimelineViewLogic
{
    /// <summary>选区最小时长（秒），与时间轴手柄拖拽下限一致。</summary>
    public const double MinSelectionSeconds = 0.2;

    private const double MinZoomChangeEpsilon = 0.0001;
    private const double MinimumFitSpanSeconds = 0.5;
    private const double MinimumRulerLabelPx = 82;

    internal static readonly double[] RulerStepsSeconds =
    {
        1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400
    };

    /// <summary>缩放后的视口：缩放倍率与可见区间起始秒。</summary>
    internal readonly record struct Viewport(double Zoom, double ViewStartSeconds);

    /// <summary>选区/播放头/视口状态收敛后的结果。</summary>
    internal readonly record struct TimelineState(
        TimeSpan InPoint,
        TimeSpan OutPoint,
        TimeSpan Position,
        double Zoom,
        double ViewStartSeconds);

    /// <summary>缩放变化是否足够大（小于阈值视为无变化，避免无谓重绘）。</summary>
    public static bool IsMeaningfulZoomChange(double currentZoom, double nextZoom) =>
        Math.Abs(nextZoom - currentZoom) >= MinZoomChangeEpsilon;

    /// <summary>
    /// 以某个锚点时间与锚点在绘图区内的横向比例（0..1）缩放，保持锚点视觉位置不变。
    /// </summary>
    public static Viewport ZoomAt(double requestedZoom, double anchorSeconds, double anchorRatio, TimeSpan duration)
    {
        var zoom = ClipTimelineGeometry.ClampZoom(requestedZoom);
        var ratio = Clamp(anchorRatio, 0, 1);
        var visibleSeconds = duration <= TimeSpan.Zero ? 0 : duration.TotalSeconds / zoom;
        return new Viewport(
            zoom,
            ClipTimelineGeometry.ClampViewStart(anchorSeconds - ratio * visibleSeconds, duration, zoom));
    }

    /// <summary>以可见区间中心为锚点缩放。</summary>
    public static Viewport ZoomToCenter(double requestedZoom, double centerSeconds, TimeSpan duration) =>
        ZoomAt(requestedZoom, centerSeconds, 0.5, duration);

    /// <summary>把视口定位到 [start, end] 区间（用于「缩放到选区」）；时长未知时返回 false。</summary>
    public static bool TryFitRange(TimeSpan duration, TimeSpan start, TimeSpan end, out Viewport viewport)
    {
        if (duration <= TimeSpan.Zero)
        {
            viewport = new Viewport(ClipTimelineGeometry.MinZoom, 0);
            return false;
        }

        var span = Math.Max(MinimumFitSpanSeconds, (end - start).TotalSeconds);
        var zoom = ClipTimelineGeometry.ClampZoom(duration.TotalSeconds / span);
        var visible = duration.TotalSeconds / zoom;
        viewport = new Viewport(
            zoom,
            ClipTimelineGeometry.ClampViewStart(start.TotalSeconds - (visible - span) / 2, duration, zoom));
        return true;
    }

    /// <summary>平移拖拽后的视口起始秒：像素位移换算成秒并夹在有效范围内。</summary>
    public static double PanViewStart(
        double viewStartAtPanStart,
        double deltaPixels,
        double plotWidth,
        TimeSpan visibleDuration,
        TimeSpan duration,
        double zoom)
    {
        if (plotWidth <= 0)
        {
            return viewStartAtPanStart;
        }

        var nextStart = viewStartAtPanStart - deltaPixels / plotWidth * visibleDuration.TotalSeconds;
        return ClipTimelineGeometry.ClampViewStart(nextStart, duration, zoom);
    }

    /// <summary>当前可见区间中心的秒数。</summary>
    public static double DisplayedCenterSeconds(double viewStartSeconds, TimeSpan visibleDuration) =>
        viewStartSeconds + visibleDuration.TotalSeconds / 2;

    /// <summary>拖拽入点手柄：限制在 [0, 出点-最小值] 并受边录边剪安全区约束。</summary>
    public static double ClampDragInPoint(double candidateSeconds, TimeSpan outPoint, TimeSpan safeDuration)
    {
        var maxIn = outPoint.TotalSeconds - MinSelectionSeconds;
        var clamped = TimeSpan.FromSeconds(Clamp(candidateSeconds, 0, Math.Max(0, maxIn)));
        return ClipTimelineGeometry.ClampToSafe(clamped, safeDuration).TotalSeconds;
    }

    /// <summary>拖拽出点手柄：限制在 [入点+最小值, 时长] 并受边录边剪安全区约束。</summary>
    public static double ClampDragOutPoint(
        double candidateSeconds,
        TimeSpan inPoint,
        TimeSpan duration,
        TimeSpan safeDuration)
    {
        var minOut = inPoint.TotalSeconds + MinSelectionSeconds;
        var maxOut = Math.Max(minOut, duration.TotalSeconds);
        if (safeDuration > TimeSpan.Zero)
        {
            maxOut = Math.Min(maxOut, Math.Max(minOut, safeDuration.TotalSeconds));
        }

        return Clamp(candidateSeconds, minOut, maxOut);
    }

    /// <summary>
    /// 把选区/播放头/视口收敛到有效范围：未知时长时全部归零；
    /// 出点越界回到末尾、入点越界归零、选区小于最小值时尽量向后扩、末尾不足时向前贴。
    /// </summary>
    public static TimelineState ClampState(
        TimeSpan duration,
        TimeSpan inPoint,
        TimeSpan outPoint,
        TimeSpan position,
        double zoom,
        double viewStartSeconds)
    {
        if (duration <= TimeSpan.Zero)
        {
            return new TimelineState(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, ClipTimelineGeometry.MinZoom, 0);
        }

        if (outPoint <= TimeSpan.Zero || outPoint > duration)
        {
            outPoint = duration;
        }

        if (inPoint < TimeSpan.Zero || inPoint > outPoint)
        {
            inPoint = TimeSpan.Zero;
        }

        if (outPoint.TotalSeconds - inPoint.TotalSeconds < MinSelectionSeconds)
        {
            if (inPoint.TotalSeconds + MinSelectionSeconds <= duration.TotalSeconds)
            {
                outPoint = TimeSpan.FromSeconds(inPoint.TotalSeconds + MinSelectionSeconds);
            }
            else
            {
                inPoint = TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds - MinSelectionSeconds));
                outPoint = duration;
            }
        }

        return new TimelineState(
            inPoint,
            outPoint,
            ClipTimelineGeometry.ClampTime(position, duration),
            zoom,
            ClipTimelineGeometry.ClampViewStart(viewStartSeconds, duration, zoom));
    }

    /// <summary>波形包络线性采样：按秒取相邻桶插值，越界取首/尾值。</summary>
    public static double SampleEnvelope(float[] data, double seconds, double bucketSeconds)
    {
        if (data.Length == 0 || bucketSeconds <= 0)
        {
            return 0;
        }

        var index = seconds / bucketSeconds;
        if (index <= 0)
        {
            return data[0];
        }

        if (index >= data.Length - 1)
        {
            return data[^1];
        }

        var i0 = (int)Math.Floor(index);
        var frac = index - i0;
        return data[i0] + (data[i0 + 1] - data[i0]) * frac;
    }

    /// <summary>按可见秒数与绘图宽度选择刻度步长：保证相邻标签至少 82px。</summary>
    public static double ChooseRulerStep(double visibleSeconds, double widthPx)
    {
        foreach (var step in RulerStepsSeconds)
        {
            if (widthPx <= 0)
            {
                return step;
            }

            if (step / Math.Max(0.0001, visibleSeconds) * widthPx >= MinimumRulerLabelPx)
            {
                return step;
            }
        }

        return RulerStepsSeconds[^1];
    }

    /// <summary>刻度时间文本：mm:ss，或 h:mm:ss。</summary>
    public static string FormatRulerTime(double seconds, bool withHours)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return withHours
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
