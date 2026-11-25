using Avalonia;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 时间轴各条行带（刻度 / 波形 / 弹幕密度 / 高能标记）的像素布局，以及整块内容区
/// （<see cref="Plot"/>）的边界。由 <see cref="ClipTimelineGeometry.ComputeLayout"/> 按控件尺寸计算。
/// </summary>
public readonly record struct TimelineLayout(
    Rect Plot,
    Rect Ruler,
    Rect Wave,
    Rect Density,
    Rect Highlight);

/// <summary>指针在时间轴上的命中目标；取值顺序即交互优先级（手柄 &gt; 高光 &gt; 播放头）。</summary>
public enum TimelineHitTarget
{
    /// <summary>未命中任何可交互目标：调用方按「点击空白处」处理（直接把播放头定位到点击位置）。</summary>
    None,

    /// <summary>命中入点手柄。</summary>
    InHandle,

    /// <summary>命中出点手柄。</summary>
    OutHandle,

    /// <summary>命中高能推荐标记（点）。</summary>
    Highlight,

    /// <summary>命中播放头。</summary>
    Playhead,
}

/// <summary>
/// <see cref="ClipTimelineControl"/> 的纯几何 / 命中计算：行带布局、视口钳制、
/// 控件 X 坐标与时间互转、入/出点手柄与高能标记命中的优先级判定。
/// <para>
/// 本类型不依赖任何 Avalonia 控件、渲染上下文或 UI 线程，只使用 <see cref="Rect"/> 等值类型，
/// 因此可以在普通单元测试里直接验证；控件本身只负责持有状态并委托到这里。
/// </para>
/// </summary>
public static class ClipTimelineGeometry
{
    /// <summary>最小缩放（1x，全片可见）。</summary>
    public const double MinZoom = 1.0;

    /// <summary>最大缩放（60x）。</summary>
    public const double MaxZoom = 60.0;

    /// <summary>入/出点手柄的水平命中容差（px）。</summary>
    public const double HandleHitTolerancePx = 7.0;

    /// <summary>播放头的水平命中容差（px）。</summary>
    public const double PlayheadHitTolerancePx = 6.0;

    /// <summary>高能标记的水平命中容差（px）。</summary>
    public const double HighlightHitTolerancePx = 8.0;

    /// <summary>高能标记行的垂直命中余量（px）：指针落在高能行上下 4px 内也算命中。</summary>
    public const double HighlightVerticalSlopPx = 4.0;

    private const double LeftPadding = 10.0;
    private const double RightPadding = 10.0;
    private const double TopPadding = 8.0;
    private const double BottomPadding = 8.0;
    private const double RulerHeight = 20.0;
    private const double RowGap = 5.0;

    /// <summary>
    /// 按控件尺寸计算时间轴布局。宽度至少 1px、内容区至少 24px 高，避免控件收缩时出现负数尺寸；
    /// 高能行至少 10px 高，保证极矮的时间轴里标记仍可见。
    /// </summary>
    /// <param name="boundsWidth">控件宽度（<c>Bounds.Width</c>）。</param>
    /// <param name="boundsHeight">控件高度（<c>Bounds.Height</c>）。</param>
    public static TimelineLayout ComputeLayout(double boundsWidth, double boundsHeight)
    {
        var width = Math.Max(1, boundsWidth - LeftPadding - RightPadding);
        var contentTop = TopPadding + RulerHeight + RowGap;
        var contentHeight = Math.Max(24, boundsHeight - contentTop - BottomPadding - RowGap * 2);
        var waveHeight = contentHeight * 0.5;
        var densityHeight = contentHeight * 0.25;
        var highlightHeight = Math.Max(10, contentHeight - waveHeight - densityHeight);

        var plot = new Rect(LeftPadding, TopPadding, width, Math.Max(24, boundsHeight - TopPadding - BottomPadding));
        var ruler = new Rect(LeftPadding, TopPadding, width, RulerHeight);
        var wave = new Rect(LeftPadding, contentTop, width, waveHeight);
        var density = new Rect(LeftPadding, wave.Bottom + RowGap, width, densityHeight);
        var highlight = new Rect(LeftPadding, density.Bottom + RowGap, width, highlightHeight);
        return new TimelineLayout(plot, ruler, wave, density, highlight);
    }

    /// <summary>把缩放系数钳制在 1x~60x。</summary>
    public static double ClampZoom(double zoom) => Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>当前缩放下的视口可见时长；1x（或更小）且时长未知时返回完整时长。</summary>
    public static TimeSpan GetVisibleDuration(TimeSpan duration, double zoom) =>
        zoom <= MinZoom || duration <= TimeSpan.Zero
            ? duration
            : TimeSpan.FromSeconds(duration.TotalSeconds / zoom);

    /// <summary>把视口起点钳制在 [0, duration - visible]；整片可见或无时长时固定为 0。</summary>
    public static double ClampViewStart(double startSeconds, TimeSpan duration, double zoom)
    {
        var visible = duration.TotalSeconds / Math.Max(MinZoom, zoom);
        var maxStart = Math.Max(0, duration.TotalSeconds - visible);
        if (maxStart <= 0)
        {
            return 0;
        }

        return Clamp(startSeconds, 0, maxStart);
    }

    /// <summary>把时间钳制到 [0, duration]；时长未知（Zero）时不设上界。</summary>
    public static TimeSpan ClampTime(TimeSpan time, TimeSpan duration)
    {
        if (time <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && time > duration)
        {
            return duration;
        }

        return time;
    }

    /// <summary>边录边剪时把时间限制在安全可剪截止时间内；未启用（Zero）时原样返回。</summary>
    public static TimeSpan ClampToSafe(TimeSpan time, TimeSpan safeDuration)
    {
        if (safeDuration > TimeSpan.Zero && time > safeDuration)
        {
            return safeDuration;
        }

        return time;
    }

    /// <summary>控件 X 坐标 → 时间：按内容区宽度换算并钳制到 [0, duration]。</summary>
    /// <param name="x">控件内 X 坐标。</param>
    /// <param name="layout">当前布局。</param>
    /// <param name="viewStartSeconds">视口起始时间（秒）。</param>
    /// <param name="visibleDuration">视口可见时长。</param>
    /// <param name="duration">视频总时长，用于钳制结果。</param>
    public static TimeSpan XToTime(
        double x,
        in TimelineLayout layout,
        double viewStartSeconds,
        TimeSpan visibleDuration,
        TimeSpan duration)
    {
        if (layout.Plot.Width <= 0)
        {
            return TimeSpan.Zero;
        }

        var ratio = (x - layout.Plot.Left) / layout.Plot.Width;
        var seconds = viewStartSeconds + ratio * visibleDuration.TotalSeconds;
        return ClampTime(TimeSpan.FromSeconds(seconds), duration);
    }

    /// <summary>时间 → 控件 X 坐标；无内容区宽度或可见时长时退化为内容区左边界。</summary>
    /// <param name="time">要换算的时间。</param>
    /// <param name="layout">当前布局。</param>
    /// <param name="viewStartSeconds">视口起始时间（秒）。</param>
    /// <param name="visibleDuration">视口可见时长。</param>
    public static double TimeToX(
        TimeSpan time,
        in TimelineLayout layout,
        double viewStartSeconds,
        TimeSpan visibleDuration)
    {
        var visible = visibleDuration.TotalSeconds;
        if (layout.Plot.Width <= 0 || visible <= 0)
        {
            return layout.Plot.Left;
        }

        return layout.Plot.Left + (time.TotalSeconds - viewStartSeconds) / visible * layout.Plot.Width;
    }

    /// <summary>
    /// 高能标记命中：指针垂直方向在高能行上下 4px 内、水平方向离标记不超过 8px 时命中，
    /// 返回列表中第一个命中的标记（绘制顺序即列表顺序）。
    /// </summary>
    public static bool TryHitHighlight(
        double x,
        double y,
        in TimelineLayout layout,
        double viewStartSeconds,
        TimeSpan visibleDuration,
        IReadOnlyList<ClipHighlightMarker>? highlights,
        out ClipHighlightMarker? marker)
    {
        marker = null;
        if (highlights is null || highlights.Count == 0)
        {
            return false;
        }

        if (y < layout.Highlight.Top - HighlightVerticalSlopPx || y > layout.Highlight.Bottom + HighlightVerticalSlopPx)
        {
            return false;
        }

        foreach (var item in highlights)
        {
            if (Math.Abs(TimeToX(item.Position, layout, viewStartSeconds, visibleDuration) - x) <= HighlightHitTolerancePx)
            {
                marker = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 指针命中判定，优先级与 <see cref="ClipTimelineControl"/> 的交互顺序一致：
    /// 入点手柄 → 出点手柄 → 高能标记 → 播放头 → 空白处（<see cref="TimelineHitTarget.None"/>）。
    /// 手柄只按水平距离判定（点击手柄所在行带之外的同一 X 位置也算命中），与原有交互行为一致。
    /// </summary>
    public static TimelineHitTarget HitTest(
        double x,
        double y,
        in TimelineLayout layout,
        double viewStartSeconds,
        TimeSpan visibleDuration,
        TimeSpan inPoint,
        TimeSpan outPoint,
        TimeSpan position,
        IReadOnlyList<ClipHighlightMarker>? highlights,
        out ClipHighlightMarker? marker)
    {
        marker = null;

        var inX = TimeToX(inPoint, layout, viewStartSeconds, visibleDuration);
        if (Math.Abs(x - inX) <= HandleHitTolerancePx)
        {
            return TimelineHitTarget.InHandle;
        }

        var outX = TimeToX(outPoint, layout, viewStartSeconds, visibleDuration);
        if (Math.Abs(x - outX) <= HandleHitTolerancePx)
        {
            return TimelineHitTarget.OutHandle;
        }

        if (TryHitHighlight(x, y, layout, viewStartSeconds, visibleDuration, highlights, out marker))
        {
            return TimelineHitTarget.Highlight;
        }

        var playheadX = TimeToX(position, layout, viewStartSeconds, visibleDuration);
        return Math.Abs(x - playheadX) <= PlayheadHitTolerancePx
            ? TimelineHitTarget.Playhead
            : TimelineHitTarget.None;
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
