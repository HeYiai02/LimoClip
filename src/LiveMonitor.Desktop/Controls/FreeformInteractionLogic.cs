using Avalonia.Input;
using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 自由画布交互的纯几何逻辑：缩放热区命中、拖动起手阈值、缩放光标映射、拖动 HUD 文案、
/// 拖放默认落位、贴合 16:9 与画布宽高比。
/// 与控件、指针事件解耦，边界情况由 <c>FreeformInteractionLogicTests</c> 覆盖；
/// 视图层只负责把结果应用到 Avalonia 控件。
/// </summary>
internal static class FreeformInteractionLogic
{
    /// <summary>缩放边热区半宽（像素）。</summary>
    internal const double ResizeEdgeHitPx = 6;

    /// <summary>拖动起手阈值（像素）：超过后才进入拖动，保留单击/双击语义给窗格内容。</summary>
    internal const double DragThresholdPx = 3;

    /// <summary>从左侧列表拖入房间时的默认落位宽（归一化）。</summary>
    internal const double DropDefaultWidth = 0.42;

    /// <summary>从左侧列表拖入房间时的默认落位高（归一化）。</summary>
    internal const double DropDefaultHeight = 0.42;

    /// <summary>贴合 16:9 时允许的最小归一化高度。</summary>
    internal const double MinimumNormalizedHeight = 0.05;

    private const double DefaultAspectRatio = 16.0 / 9.0;

    /// <summary>指针相对按下点是否已超过拖动阈值。</summary>
    internal static bool ExceedsDragThreshold(double deltaX, double deltaY)
        => deltaX * deltaX + deltaY * deltaY >= DragThresholdPx * DragThresholdPx;

    /// <summary>按画布坐标命中窗格的缩放边；落在热区外返回 <see cref="ResizeEdge.None"/>。</summary>
    internal static ResizeEdge HitTestResizeEdge(
        NormalizedRect rect,
        double canvasWidth,
        double canvasHeight,
        double x,
        double y)
    {
        var (left, top, width, height) = rect.ToPixels(canvasWidth, canvasHeight);
        var right = left + width;
        var bottom = top + height;

        var nearLeft = Math.Abs(x - left) <= ResizeEdgeHitPx;
        var nearRight = Math.Abs(x - right) <= ResizeEdgeHitPx;
        var nearTop = Math.Abs(y - top) <= ResizeEdgeHitPx;
        var nearBottom = Math.Abs(y - bottom) <= ResizeEdgeHitPx;

        var insideX = x >= left - ResizeEdgeHitPx && x <= right + ResizeEdgeHitPx;
        var insideY = y >= top - ResizeEdgeHitPx && y <= bottom + ResizeEdgeHitPx;
        if (!insideX || !insideY)
        {
            return ResizeEdge.None;
        }

        if (nearLeft && nearTop)
        {
            return ResizeEdge.TopLeft;
        }

        if (nearRight && nearTop)
        {
            return ResizeEdge.TopRight;
        }

        if (nearLeft && nearBottom)
        {
            return ResizeEdge.BottomLeft;
        }

        if (nearRight && nearBottom)
        {
            return ResizeEdge.BottomRight;
        }

        if (nearLeft)
        {
            return ResizeEdge.Left;
        }

        if (nearRight)
        {
            return ResizeEdge.Right;
        }

        if (nearTop)
        {
            return ResizeEdge.Top;
        }

        return nearBottom ? ResizeEdge.Bottom : ResizeEdge.None;
    }

    /// <summary>缩放/移动时鼠标光标：移动统一 SizeAll；缩放按命中边映射四向或对角光标。</summary>
    internal static StandardCursorType MapResizeCursor(ResizeEdge edge, bool moving) => moving
        ? StandardCursorType.SizeAll
        : edge switch
        {
            ResizeEdge.Left or ResizeEdge.Right => StandardCursorType.SizeWestEast,
            ResizeEdge.Top or ResizeEdge.Bottom => StandardCursorType.SizeNorthSouth,
            ResizeEdge.TopLeft or ResizeEdge.BottomRight => StandardCursorType.TopLeftCorner,
            ResizeEdge.TopRight or ResizeEdge.BottomLeft => StandardCursorType.TopRightCorner,
            _ => StandardCursorType.SizeAll
        };

    /// <summary>拖动 HUD 文案：像素尺寸 + 归一化百分比，有吸附时追加「已吸附」。</summary>
    internal static string BuildDragHud(
        NormalizedRect rect,
        double canvasWidth,
        double canvasHeight,
        bool snapped)
    {
        var text = $"{Math.Round(rect.Width * canvasWidth):0} × {Math.Round(rect.Height * canvasHeight):0} px"
            + $" · {rect.Width * 100:0}% × {rect.Height * 100:0}%";
        return snapped ? text + " · 已吸附" : text;
    }

    /// <summary>把房间从左侧列表拖入画布时的建议落位：以指针为中心、默认 42% 见方并约束在画布内。</summary>
    internal static NormalizedRect ComputeDropRect(
        double x,
        double y,
        double canvasWidth,
        double canvasHeight)
    {
        const double width = DropDefaultWidth;
        const double height = DropDefaultHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return new NormalizedRect(0.3, 0.3, width, height);
        }

        var left = x / canvasWidth - width / 2;
        var top = y / canvasHeight - height / 2;
        return new NormalizedRect(
            Math.Clamp(left, 0, 1 - width),
            Math.Clamp(top, 0, 1 - height),
            width,
            height);
    }

    /// <summary>把窗格高度调成 16:9（按当前宽度换算），必要时把窗格上移回画布内。</summary>
    internal static NormalizedRect FitTo16By9(NormalizedRect rect, double canvasWidth, double canvasHeight)
    {
        if (canvasHeight <= 0)
        {
            return rect;
        }

        var widthPx = rect.Width * canvasWidth;
        var desiredHeightNorm = widthPx * 9.0 / 16.0 / canvasHeight;
        var height = Math.Clamp(desiredHeightNorm, MinimumNormalizedHeight, 1.0);
        var top = rect.Top + height > 1.0 ? Math.Max(0, 1.0 - height) : rect.Top;
        return rect with { Top = top, Height = height };
    }

    /// <summary>画布宽高比；尺寸未知时回退 16:9。</summary>
    internal static double AspectOf(double width, double height) =>
        width > 0 && height > 0 ? width / height : DefaultAspectRatio;
}
