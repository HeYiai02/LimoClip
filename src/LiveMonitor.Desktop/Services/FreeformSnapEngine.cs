namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 自由画布磁吸对齐引擎：纯托管、无 UI 依赖，可直接单测。
/// 同时支持「移动吸附」与「8 方向缩放吸附」，并负责最小尺寸与画布边界保护。
/// </summary>
public static class FreeformSnapEngine
{
    /// <summary>磁吸阈值（屏幕像素）：移动/缩放时距离目标 8px 内自动吸附。</summary>
    public const double DefaultThresholdPx = 8.0;

    /// <summary>窗格最小宽度（像素）：防止拖到看不见。</summary>
    public const double DefaultMinWidthPx = 180.0;

    /// <summary>窗格最小高度（像素）：防止拖成一条线。</summary>
    public const double DefaultMinHeightPx = 100.0;

    /// <summary>归一化最小尺寸下限：窗口极小时兜底。</summary>
    private const double MinNormalizedFloor = 0.05;

    /// <summary>
    /// 移动窗格时的磁吸计算：吸附画布边界/中线、其它窗格的边缘与中线。
    /// <paramref name="bypassSnap"/> 为 true（Ctrl 按住）时只做边界与最小尺寸约束。
    /// </summary>
    public static SnapResult SnapMove(
        NormalizedRect raw,
        IReadOnlyList<NormalizedRect> others,
        double canvasWidthPx,
        double canvasHeightPx,
        double thresholdPx = DefaultThresholdPx,
        bool bypassSnap = false,
        double minWidthPx = DefaultMinWidthPx,
        double minHeightPx = DefaultMinHeightPx)
    {
        var minW = NormalizedMin(canvasWidthPx, minWidthPx, 0.05);
        var minH = NormalizedMin(canvasHeightPx, minHeightPx, 0.05);

        double w = Math.Clamp(raw.Width, minW, 1.0);
        double h = Math.Clamp(raw.Height, minH, 1.0);
        double left = Math.Clamp(raw.Left, 0.0, 1.0 - w);
        double top = Math.Clamp(raw.Top, 0.0, 1.0 - h);

        if (bypassSnap || canvasWidthPx <= 0 || canvasHeightPx <= 0)
        {
            return new SnapResult(new NormalizedRect(left, top, w, h), Array.Empty<SnapGuideLine>());
        }

        double thresholdX = thresholdPx / canvasWidthPx;
        double thresholdY = thresholdPx / canvasHeightPx;
        var guides = new List<SnapGuideLine>(2);

        // ---- X 轴吸附：候选 = 画布左/右/中线 + others 的左/右/中线 ----
        double bestDx = double.MaxValue;
        SnapGuideLine? bestXGuide = null;

        void TrySnapX(double targetX, double sourceX, double segStart, double segEnd, bool isCenter)
        {
            var delta = targetX - sourceX;
            if (Math.Abs(delta) <= thresholdX && Math.Abs(delta) < Math.Abs(bestDx))
            {
                bestDx = delta;
                bestXGuide = new SnapGuideLine(true, targetX, segStart, segEnd, isCenter);
            }
        }

        TrySnapX(0.0, left, 0.0, 1.0, false);
        TrySnapX(1.0, left + w, 0.0, 1.0, false);
        TrySnapX(0.5, left + w * 0.5, 0.0, 1.0, true);

        foreach (var other in others)
        {
            double segStart = Math.Min(top, other.Top);
            double segEnd = Math.Max(top + h, other.Bottom);

            TrySnapX(other.Left, left, segStart, segEnd, false);
            TrySnapX(other.Right, left, segStart, segEnd, false);
            TrySnapX(other.Left, left + w, segStart, segEnd, false);
            TrySnapX(other.Right, left + w, segStart, segEnd, false);
            TrySnapX(other.CenterX, left + w * 0.5, segStart, segEnd, true);
        }

        if (bestXGuide is not null)
        {
            left = Math.Clamp(left + bestDx, 0.0, 1.0 - w);
            guides.Add(bestXGuide.Value);
        }

        // ---- Y 轴吸附：候选 = 画布上/下/中线 + others 的上/下/中线 ----
        double bestDy = double.MaxValue;
        SnapGuideLine? bestYGuide = null;

        void TrySnapY(double targetY, double sourceY, double segStart, double segEnd, bool isCenter)
        {
            var delta = targetY - sourceY;
            if (Math.Abs(delta) <= thresholdY && Math.Abs(delta) < Math.Abs(bestDy))
            {
                bestDy = delta;
                bestYGuide = new SnapGuideLine(false, targetY, segStart, segEnd, isCenter);
            }
        }

        TrySnapY(0.0, top, 0.0, 1.0, false);
        TrySnapY(1.0, top + h, 0.0, 1.0, false);
        TrySnapY(0.5, top + h * 0.5, 0.0, 1.0, true);

        foreach (var other in others)
        {
            double segStart = Math.Min(left, other.Left);
            double segEnd = Math.Max(left + w, other.Right);

            TrySnapY(other.Top, top, segStart, segEnd, false);
            TrySnapY(other.Bottom, top, segStart, segEnd, false);
            TrySnapY(other.Top, top + h, segStart, segEnd, false);
            TrySnapY(other.Bottom, top + h, segStart, segEnd, false);
            TrySnapY(other.CenterY, top + h * 0.5, segStart, segEnd, true);
        }

        if (bestYGuide is not null)
        {
            top = Math.Clamp(top + bestDy, 0.0, 1.0 - h);
            guides.Add(bestYGuide.Value);
        }

        return new SnapResult(new NormalizedRect(left, top, w, h), guides);
    }

    /// <summary>
    /// 8 方向缩放时的磁吸计算：拖动中的边吸附到画布/其它窗格的边与中线，
    /// 并保持最小尺寸；<paramref name="lockAspect16By9"/>（Shift 按住）时按像素宽高比锁定 16:9。
    /// </summary>
    public static SnapResult SnapResize(
        NormalizedRect origin,
        ResizeEdge edge,
        double deltaXNorm,
        double deltaYNorm,
        IReadOnlyList<NormalizedRect> others,
        double canvasWidthPx,
        double canvasHeightPx,
        double minWidthPx = DefaultMinWidthPx,
        double minHeightPx = DefaultMinHeightPx,
        double thresholdPx = DefaultThresholdPx,
        bool bypassSnap = false,
        bool lockAspect16By9 = false)
    {
        var minW = NormalizedMin(canvasWidthPx, minWidthPx, 0.12);
        var minH = NormalizedMin(canvasHeightPx, minHeightPx, 0.12);

        double left = origin.Left;
        double right = origin.Right;
        double top = origin.Top;
        double bottom = origin.Bottom;

        if (edge.HasFlag(ResizeEdge.Left))
        {
            left = Math.Clamp(origin.Left + deltaXNorm, 0.0, right - minW);
        }

        if (edge.HasFlag(ResizeEdge.Right))
        {
            right = Math.Clamp(origin.Right + deltaXNorm, left + minW, 1.0);
        }

        if (edge.HasFlag(ResizeEdge.Top))
        {
            top = Math.Clamp(origin.Top + deltaYNorm, 0.0, bottom - minH);
        }

        if (edge.HasFlag(ResizeEdge.Bottom))
        {
            bottom = Math.Clamp(origin.Bottom + deltaYNorm, top + minH, 1.0);
        }

        var guides = new List<SnapGuideLine>(2);

        if (!bypassSnap && canvasWidthPx > 0 && canvasHeightPx > 0)
        {
            double thresholdX = thresholdPx / canvasWidthPx;
            double thresholdY = thresholdPx / canvasHeightPx;

            // 水平方向：只有左右边在拖动时才吸附
            if (edge.HasFlag(ResizeEdge.Left) || edge.HasFlag(ResizeEdge.Right))
            {
                bool isLeft = edge.HasFlag(ResizeEdge.Left);
                double currentX = isLeft ? left : right;
                double bestX = currentX;
                double bestDistance = thresholdX + 1e-9;
                SnapGuideLine? guide = null;

                void TrySnapX(double target, double segStart, double segEnd, bool isCenter)
                {
                    var distance = Math.Abs(target - currentX);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestX = target;
                        guide = new SnapGuideLine(true, target, segStart, segEnd, isCenter);
                    }
                }

                TrySnapX(0.0, 0.0, 1.0, false);
                TrySnapX(0.5, 0.0, 1.0, true);
                TrySnapX(1.0, 0.0, 1.0, false);

                foreach (var other in others)
                {
                    double segStart = Math.Min(top, other.Top);
                    double segEnd = Math.Max(bottom, other.Bottom);
                    TrySnapX(other.Left, segStart, segEnd, false);
                    TrySnapX(other.Right, segStart, segEnd, false);
                    TrySnapX(other.CenterX, segStart, segEnd, true);
                }

                if (guide is not null)
                {
                    if (isLeft && right - bestX >= minW)
                    {
                        left = bestX;
                        guides.Add(guide.Value);
                    }
                    else if (!isLeft && bestX - left >= minW)
                    {
                        right = bestX;
                        guides.Add(guide.Value);
                    }
                }
            }

            // 垂直方向：只有上下边在拖动时才吸附
            if (edge.HasFlag(ResizeEdge.Top) || edge.HasFlag(ResizeEdge.Bottom))
            {
                bool isTop = edge.HasFlag(ResizeEdge.Top);
                double currentY = isTop ? top : bottom;
                double bestY = currentY;
                double bestDistance = thresholdY + 1e-9;
                SnapGuideLine? guide = null;

                void TrySnapY(double target, double segStart, double segEnd, bool isCenter)
                {
                    var distance = Math.Abs(target - currentY);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestY = target;
                        guide = new SnapGuideLine(false, target, segStart, segEnd, isCenter);
                    }
                }

                TrySnapY(0.0, 0.0, 1.0, false);
                TrySnapY(0.5, 0.0, 1.0, true);
                TrySnapY(1.0, 0.0, 1.0, false);

                foreach (var other in others)
                {
                    double segStart = Math.Min(left, other.Left);
                    double segEnd = Math.Max(right, other.Right);
                    TrySnapY(other.Top, segStart, segEnd, false);
                    TrySnapY(other.Bottom, segStart, segEnd, false);
                    TrySnapY(other.CenterY, segStart, segEnd, true);
                }

                if (guide is not null)
                {
                    if (isTop && bottom - bestY >= minH)
                    {
                        top = bestY;
                        guides.Add(guide.Value);
                    }
                    else if (!isTop && bestY - top >= minH)
                    {
                        bottom = bestY;
                        guides.Add(guide.Value);
                    }
                }
            }
        }

        // Shift 按住：按实际像素锁定 16:9（只改高度，宽度保持不变）
        if (lockAspect16By9 && canvasWidthPx > 0 && canvasHeightPx > 0)
        {
            double widthPx = (right - left) * canvasWidthPx;
            double targetHeight = (widthPx * 9.0 / 16.0) / canvasHeightPx;
            targetHeight = Math.Clamp(targetHeight, minH, 1.0);

            if (edge.HasFlag(ResizeEdge.Top))
            {
                top = Math.Clamp(bottom - targetHeight, 0.0, bottom - minH);
            }
            else
            {
                bottom = Math.Clamp(top + targetHeight, top + minH, 1.0);
            }
        }

        return new SnapResult(new NormalizedRect(left, top, right - left, bottom - top), guides);
    }

    private static double NormalizedMin(double canvasPixels, double minPixels, double fallback)
    {
        if (canvasPixels <= 0)
        {
            return fallback;
        }

        return Math.Clamp(minPixels / canvasPixels, MinNormalizedFloor, 0.95);
    }
}
