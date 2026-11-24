using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveMonitor.Desktop.Services;
using Color = Avalonia.Media.Color;
using Control = Avalonia.Controls.Control;
using FontFamily = Avalonia.Media.FontFamily;
using Pen = Avalonia.Media.Pen;
using Point = Avalonia.Point;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 磁吸辅助线 + 拖动反馈浮层：画在自由画布最上层，不参与命中测试。
/// 普通边/中线为蓝色实线，画布或窗格中心线为琥珀色虚线；拖动中的窗格另外显示落位框和尺寸提示。
/// </summary>
public sealed class SnapGuidesOverlay : Control
{
    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush CenterGuideBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly Pen GuidePen = new(GuideBrush, 1.5);
    private static readonly Pen CenterGuidePen = new(CenterGuideBrush, 1.5, dashStyle: new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IBrush ActiveFillBrush = new SolidColorBrush(Color.Parse("#333B82F6"));
    private static readonly Pen ActiveBorderPen = new(new SolidColorBrush(Color.Parse("#3B82F6")), 2);
    private static readonly IBrush HudBackgroundBrush = new SolidColorBrush(Color.Parse("#F20B0E14"));
    private static readonly Pen HudBorderPen = new(new SolidColorBrush(Color.Parse("#3B82F6")), 1);
    private static readonly IBrush HudTextBrush = new SolidColorBrush(Color.Parse("#E6E8EE"));
    private static readonly Typeface HudTypeface =
        new(FontFamily.Parse("Microsoft YaHei UI, Segoe UI, sans-serif"));

    private IReadOnlyList<SnapGuideLine> _guides = Array.Empty<SnapGuideLine>();
    private NormalizedRect? _activeRect;
    private string? _hudText;
    private Point _hudPosition;

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

    /// <summary>设置拖动中窗格的实时落位框（归一化坐标）。</summary>
    public void SetActiveRect(NormalizedRect? rect)
    {
        _activeRect = rect;
        InvalidateVisual();
    }

    /// <summary>设置跟随指针的尺寸/吸附提示文本；position 为画布内像素坐标。</summary>
    public void SetHud(string? text, Point position)
    {
        _hudText = text;
        _hudPosition = position;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (_activeRect is { } active)
        {
            var rect = new Rect(
                active.Left * width,
                active.Top * height,
                active.Width * width,
                active.Height * height);
            context.DrawRectangle(ActiveFillBrush, ActiveBorderPen, rect);
        }

        foreach (var guide in _guides)
        {
            var pen = guide.IsCenterGuide ? CenterGuidePen : GuidePen;
            if (guide.IsVertical)
            {
                var x = Math.Clamp(guide.Position, 0, 1) * width;
                var startY = Math.Clamp(guide.Start, 0, 1) * height;
                var endY = Math.Clamp(guide.End, 0, 1) * height;
                context.DrawLine(pen, new Point(x, startY), new Point(x, endY));
                // 端帽：让细线在深色画面上更醒目
                context.DrawLine(pen, new Point(x - 4, startY), new Point(x + 4, startY));
                context.DrawLine(pen, new Point(x - 4, endY), new Point(x + 4, endY));
            }
            else
            {
                var y = Math.Clamp(guide.Position, 0, 1) * height;
                var startX = Math.Clamp(guide.Start, 0, 1) * width;
                var endX = Math.Clamp(guide.End, 0, 1) * width;
                context.DrawLine(pen, new Point(startX, y), new Point(endX, y));
                context.DrawLine(pen, new Point(startX, y - 4), new Point(startX, y + 4));
                context.DrawLine(pen, new Point(endX, y - 4), new Point(endX, y + 4));
            }
        }

        DrawHud(context, width, height);
    }

    private void DrawHud(DrawingContext context, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(_hudText))
        {
            return;
        }

        var text = new FormattedText(
            _hudText,
            CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight,
            HudTypeface,
            12,
            HudTextBrush);

        const double paddingX = 8;
        const double paddingY = 4;
        var boxWidth = text.Width + paddingX * 2;
        var boxHeight = text.Height + paddingY * 2;

        var x = Math.Clamp(_hudPosition.X, 0, Math.Max(0, width - boxWidth));
        var y = Math.Clamp(_hudPosition.Y, 0, Math.Max(0, height - boxHeight));
        var box = new Rect(x, y, boxWidth, boxHeight);
        context.DrawRectangle(HudBackgroundBrush, HudBorderPen, box, 4, 4);
        context.DrawText(text, new Point(x + paddingX, y + paddingY));
    }
}
