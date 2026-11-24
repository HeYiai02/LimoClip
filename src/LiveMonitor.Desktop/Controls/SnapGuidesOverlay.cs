using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveMonitor.Desktop.Services;
using Color = Avalonia.Media.Color;
using Control = Avalonia.Controls.Control;
using Pen = Avalonia.Media.Pen;
using Point = Avalonia.Point;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 磁吸辅助线浮层：画在自由画布最上层，不参与命中测试。
/// 普通边/中线为蓝色，画布或窗格中心线为琥珀色。
/// </summary>
public sealed class SnapGuidesOverlay : Control
{
    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush CenterGuideBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly Pen GuidePen = new(GuideBrush, 1);
    private static readonly Pen CenterGuidePen = new(CenterGuideBrush, 1);

    private IReadOnlyList<SnapGuideLine> _guides = Array.Empty<SnapGuideLine>();

    static SnapGuidesOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<SnapGuidesOverlay>(false);
        ClipToBoundsProperty.OverrideDefaultValue<SnapGuidesOverlay>(false);
    }

    public void SetGuides(IReadOnlyList<SnapGuideLine>? guides)
    {
        _guides = guides ?? Array.Empty<SnapGuideLine>();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_guides.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        foreach (var guide in _guides)
        {
            var pen = guide.IsCenterGuide ? CenterGuidePen : GuidePen;
            if (guide.IsVertical)
            {
                var x = Math.Clamp(guide.Position, 0, 1) * width;
                var startY = Math.Clamp(guide.Start, 0, 1) * height;
                var endY = Math.Clamp(guide.End, 0, 1) * height;
                context.DrawLine(pen, new Point(x, startY), new Point(x, endY));
            }
            else
            {
                var y = Math.Clamp(guide.Position, 0, 1) * height;
                var startX = Math.Clamp(guide.Start, 0, 1) * width;
                var endX = Math.Clamp(guide.End, 0, 1) * width;
                context.DrawLine(pen, new Point(startX, y), new Point(endX, y));
            }
        }
    }
}
