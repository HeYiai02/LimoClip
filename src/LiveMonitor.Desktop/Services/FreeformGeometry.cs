namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 自由画布的唯一坐标表示：相对画布宽高的归一化矩形（0~1）。
/// 严禁持久化像素值，保证折叠侧栏 / 缩放窗口 / 换分辨率 / 桌面全屏时布局不错位。
/// </summary>
public readonly record struct NormalizedRect(double Left, double Top, double Width, double Height)
{
    public static NormalizedRect FullCanvas => new(0, 0, 1, 1);

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double CenterX => Left + Width * 0.5;

    public double CenterY => Top + Height * 0.5;

    /// <summary>把矩形约束在画布内，并保证最小尺寸（默认 5%）。</summary>
    public NormalizedRect ClampToCanvas(double minWidth = 0.05, double minHeight = 0.05)
    {
        var width = Math.Clamp(Width, minWidth, 1.0);
        var height = Math.Clamp(Height, minHeight, 1.0);
        var left = Math.Clamp(Left, 0.0, 1.0 - width);
        var top = Math.Clamp(Top, 0.0, 1.0 - height);
        return new NormalizedRect(left, top, width, height);
    }

    public NormalizedRect WithPosition(double left, double top) => this with { Left = left, Top = top };

    public (double X, double Y, double Width, double Height) ToPixels(double canvasWidth, double canvasHeight) =>
        (Left * canvasWidth, Top * canvasHeight, Width * canvasWidth, Height * canvasHeight);

    public static NormalizedRect FromPixels(
        double x,
        double y,
        double width,
        double height,
        double canvasWidth,
        double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return FullCanvas;
        }

        var rect = new NormalizedRect(x / canvasWidth, y / canvasHeight, width / canvasWidth, height / canvasHeight);
        return rect.ClampToCanvas();
    }

    /// <summary>两个矩形是否相交（用于预设生成后的自检与测试）。</summary>
    public bool IntersectsWith(NormalizedRect other, double epsilon = 1e-9) =>
        Left < other.Right - epsilon &&
        Right > other.Left + epsilon &&
        Top < other.Bottom - epsilon &&
        Bottom > other.Top + epsilon;
}

/// <summary>8 方向缩放的命中边（可用 HasFlag 组合判断角）。</summary>
[Flags]
public enum ResizeEdge
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Top = 1 << 2,
    Bottom = 1 << 3,
    TopLeft = (1 << 2) | (1 << 0),
    TopRight = (1 << 2) | (1 << 1),
    BottomLeft = (1 << 3) | (1 << 0),
    BottomRight = (1 << 3) | (1 << 1)
}

/// <summary>一条磁吸辅助线（归一化坐标；顶层 <c>SnapGuidesOverlay</c> 负责绘制）。</summary>
public readonly record struct SnapGuideLine(
    bool IsVertical,
    double Position,
    double Start,
    double End,
    bool IsCenterGuide);

/// <summary>磁吸计算结果：修正后的矩形 + 需要绘制的辅助线。</summary>
public sealed record SnapResult(NormalizedRect Rect, IReadOnlyList<SnapGuideLine> Guides)
{
    public static readonly SnapResult Empty = new(NormalizedRect.FullCanvas, Array.Empty<SnapGuideLine>());
}
