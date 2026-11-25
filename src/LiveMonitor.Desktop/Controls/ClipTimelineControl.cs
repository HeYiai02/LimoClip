using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LiveMonitor.Core.Models;
using Control = Avalonia.Controls.Control;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;
using Color = Avalonia.Media.Color;
using Cursor = Avalonia.Input.Cursor;
using StandardCursorType = Avalonia.Input.StandardCursorType;
using DrawingContext = Avalonia.Media.DrawingContext;
using FormattedText = Avalonia.Media.FormattedText;
using IBrush = Avalonia.Media.IBrush;
using IPen = Avalonia.Media.IPen;
using Pen = Avalonia.Media.Pen;
using SolidColorBrush = Avalonia.Media.SolidColorBrush;
using StreamGeometry = Avalonia.Media.StreamGeometry;
using Typeface = Avalonia.Media.Typeface;
using FlowDirection = Avalonia.Media.FlowDirection;
using PointerEventArgs = Avalonia.Input.PointerEventArgs;
using PointerPressedEventArgs = Avalonia.Input.PointerPressedEventArgs;
using PointerReleasedEventArgs = Avalonia.Input.PointerReleasedEventArgs;
using PointerWheelEventArgs = Avalonia.Input.PointerWheelEventArgs;
using PointerCaptureLostEventArgs = Avalonia.Input.PointerCaptureLostEventArgs;

namespace LiveMonitor.Desktop.Controls;

/// <summary>时间轴上的一个高能推荐标记（点）。</summary>
public sealed record ClipHighlightMarker(TimeSpan Position, string? Reason = null);

/// <summary>
/// 剪辑台底部自绘双波形时间轴：时间刻度 + 音频峰值包络 + 弹幕密度热力 + 高能标记 +
/// 入/出点选区 + 播放头。支持滚轮以光标为锚点缩放（1x~60x）、右键/中键拖动平移、
/// 拖拽入/出点、点击/拖拽播放头、双击复位视口。
/// <para>
/// 纯 CLR 属性 + <see cref="Control.InvalidateVisual"/>，由使用方（ClipStudioView）直接赋值；
/// 所有公开属性只在 UI 线程读写。
/// </para>
/// </summary>
public sealed class ClipTimelineControl : Control
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 60.0;
    private const double HandleHitTolerancePx = 7.0;
    private const double PlayheadHitTolerancePx = 6.0;
    private const double MinSelectionSeconds = 0.2;
    private const double LeftPadding = 10.0;
    private const double RightPadding = 10.0;
    private const double TopPadding = 8.0;
    private const double BottomPadding = 8.0;
    private const double RulerHeight = 20.0;
    private const double RowGap = 5.0;

    // ---- 暗房调色板：与 Styles/DesignTokens.axaml 的 token 保持一致 ----

    /// <summary>时间轴底色（比卡片更深一档，形成凹陷的暗房监视器质感）。</summary>
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0A0C10"));
    private static readonly IBrush RulerBackgroundBrush = new SolidColorBrush(Color.Parse("#0E1117"));
    private static readonly IBrush RowBackgroundBrush = new SolidColorBrush(Color.Parse("#0C0F15"));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#242A37")); // BrushBorderSubtle
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#1B2230"));

    /// <summary>刻度数字 / 说明文字（BrushTextMuted）。</summary>
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#64748B"));

    /// <summary>悬停时间码等次级文字（BrushTextSecondary）。</summary>
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

    /// <summary>波形主体：非选区 alpha 0.28，选区内 alpha 0.72（聚光灯）。</summary>
    private static readonly IBrush WaveDimBrush = new SolidColorBrush(Color.FromArgb(0x47, 0x22, 0xD3, 0xEE));
    private static readonly IBrush WaveBrightBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0x22, 0xD3, 0xEE));

    /// <summary>波形中线（低透明度青色，压在波形柱下方只从柱间缝隙透出）。</summary>
    private static readonly IBrush WaveTipBrush = new SolidColorBrush(Color.Parse("#55ECFEFF"));

    /// <summary>弹幕密度热力：Fill 顶部 #F59E0B alpha 0.45 → 底部 0；描边固定 BrushWarning。</summary>
    private static readonly IBrush DensityFillBrush = CreateDensityFillBrush();
    private static readonly IBrush DensityOutlineBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

    private static readonly IBrush HighlightFillBrush = new SolidColorBrush(Color.Parse("#26EF4444"));
    private static readonly IBrush HighlightBorderBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush SelectionFillBrush = new SolidColorBrush(Color.Parse("#2E3B82F6"));
    private static readonly IBrush SelectionBorderBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.Parse("#3305070A")); // 20% 近黑，抬高选区对比
    private static readonly IBrush LiveEdgeMaskBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0x94, 0xA3, 0xB8)); // 边录边剪：极淡灰安全区外遮罩
    private static readonly IPen LiveEdgeStripePen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8)), 1); // 45° 细斜纹
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.Parse("#EF4444")); // BrushDanger
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.Parse("#665A6B85"));
    private static readonly IBrush HandleLabelBrush = new SolidColorBrush(Color.Parse("#DBEAFE"));
    private static readonly IBrush ZoomBadgeBrush = new SolidColorBrush(Color.Parse("#CC11141B")); // BrushBgBase 80%
    private static readonly IBrush PlayheadBubbleTextBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IPen BorderPen = new Pen(BorderBrush, 1);
    private static readonly IPen GridPen = new Pen(GridBrush, 1);
    private static readonly IPen WaveGuidePen = new Pen(WaveTipBrush, 1);
    private static readonly IPen DensityOutlinePen = new Pen(DensityOutlineBrush, 1);
    private static readonly IPen SelectionBorderPen = new Pen(SelectionBorderBrush, 1.25);
    private static readonly IPen PlayheadPen = new Pen(PlayheadBrush, 1.5);
    private static readonly IPen HoverPen = new Pen(HoverBrush, 1);
    private static readonly IPen HighlightBorderPen = new Pen(HighlightBorderBrush, 1);

    /// <summary>与 FontMono token 一致的等宽字体（自绘文本无法直接解析 DynamicResource）。</summary>
    private static readonly Avalonia.Media.FontFamily MonoFontFamily =
        new Avalonia.Media.FontFamily("Cascadia Mono, JetBrains Mono, Consolas, monospace");

    private static readonly Typeface DefaultTypeface = new Typeface(MonoFontFamily);

    // 波形柱几何参数：2px 采样步长、1.5px 柱宽、0.75px 轻微圆角。
    private const double BarStepPx = 2.0;
    private const double BarWidthPx = 1.5;
    private const double BarRadiusPx = 0.75;

    /// <summary>弹幕热力填充：顶部 #F59E0B alpha 0.45，向下线性衰减到 0。</summary>
    private static IBrush CreateDensityFillBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x73, 0xF5, 0x9E, 0x0B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xF5, 0x9E, 0x0B), 1));
        return brush;
    }

    private static readonly double[] RulerStepsSeconds =
    {
        1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400
    };

    private WaveformData? _waveform;
    private TimeSpan _duration;
    private TimeSpan _position;
    private TimeSpan _inPoint;
    private TimeSpan _outPoint;
    private TimeSpan _safeDuration;
    private IReadOnlyList<ClipHighlightMarker>? _highlights;
    private double _zoom = MinZoom;
    private double _viewStartSeconds;

    private DragMode _dragMode;
    private double _panStartX;
    private double _viewStartAtPanStart;
    private double? _hoverX;

    private enum DragMode
    {
        None,
        In,
        Out,
        Playhead,
        Pan
    }

    public ClipTimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        MinHeight = 96;
    }

    /// <summary>波形数据（音频峰值 + 弹幕密度）。null 表示尚未加载完成。</summary>
    public WaveformData? Waveform
    {
        get => _waveform;
        set
        {
            if (ReferenceEquals(_waveform, value))
            {
                return;
            }

            _waveform = value;
            InvalidateVisual();
        }
    }

    /// <summary>视频总时长。</summary>
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            // 注意：这里不能用 ClampTime —— 边录边剪同步时 Duration 需要逐步增长，
            // 而 ClampTime 会把超出旧时长的新值钳回旧值，导致时间轴永远停在初始时长。
            var next = value <= TimeSpan.Zero ? TimeSpan.Zero : value;
            var firstSet = _duration <= TimeSpan.Zero && next > TimeSpan.Zero;
            if (next == _duration && !firstSet)
            {
                return;
            }

            _duration = next;
            if (firstSet || _outPoint <= TimeSpan.Zero || _outPoint > next)
            {
                _inPoint = TimeSpan.Zero;
                _outPoint = next;
            }

            ClampState();
            InvalidateVisual();
        }
    }

    /// <summary>播放头位置。</summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            var next = ClampToSafe(ClampTime(value));
            if (next == _position)
            {
                return;
            }

            _position = next;
            InvalidateVisual();
        }
    }

    /// <summary>入点（选区起点）。</summary>
    public TimeSpan InPoint
    {
        get => _inPoint;
        set
        {
            var next = ClampToSafe(ClampTime(value));
            var maxIn = Math.Max(0, _outPoint.TotalSeconds - MinSelectionSeconds);
            if (next.TotalSeconds > maxIn)
            {
                next = TimeSpan.FromSeconds(maxIn);
            }

            if (next == _inPoint)
            {
                return;
            }

            _inPoint = next;
            InvalidateVisual();
        }
    }

    /// <summary>出点（选区终点）。</summary>
    public TimeSpan OutPoint
    {
        get => _outPoint;
        set
        {
            var next = ClampToSafe(ClampTime(value));
            var minOut = _inPoint.TotalSeconds + MinSelectionSeconds;
            if (next.TotalSeconds < minOut)
            {
                next = TimeSpan.FromSeconds(minOut);
            }

            next = ClampToSafe(next);

            if (next == _outPoint)
            {
                return;
            }

            _outPoint = next;
            InvalidateVisual();
        }
    }

    /// <summary>高能推荐标记（可为空）。</summary>
    public IReadOnlyList<ClipHighlightMarker>? Highlights
    {
        get => _highlights;
        set
        {
            _highlights = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 边录边剪的安全可剪截止时间：默认 Zero 表示不启用；
    /// 启用时在 [SafeDuration, Duration) 区间绘制极淡遮罩 + 斜纹，提示该段尚未安全写入。
    /// </summary>
    public TimeSpan SafeDuration
    {
        get => _safeDuration;
        set
        {
            var next = ClampTime(value);
            if (next == _safeDuration)
            {
                return;
            }

            _safeDuration = next;
            InvalidateVisual();
        }
    }

    /// <summary>当前缩放系数（1x~60x）。</summary>
    public double Zoom => _zoom;

    /// <summary>当前视口起始时间。</summary>
    public TimeSpan ViewStart => TimeSpan.FromSeconds(_viewStartSeconds);

    /// <summary>当前视口可见时长。</summary>
    public TimeSpan VisibleDuration =>
        _zoom <= MinZoom || _duration <= TimeSpan.Zero
            ? _duration
            : TimeSpan.FromSeconds(_duration.TotalSeconds / _zoom);

    /// <summary>入/出点被用户拖动后触发。</summary>
    public event EventHandler? InOutChanged;

    /// <summary>播放头拖动结束（或点击某处）后请求定位播放位置。</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>缩放或平移视口后触发。</summary>
    public event EventHandler? ViewChanged;

    /// <summary>设置缩放并可选指定锚点时间（保持锚点视觉位置不变）。</summary>
    public void SetZoom(double zoom, TimeSpan? anchor = null)
    {
        var target = Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(target - _zoom) < 0.0001)
        {
            return;
        }

        var layout = GetLayout();
        var anchorTime = anchor?.TotalSeconds ?? DisplayedCenterSeconds();
        var ratio = 0.5;
        if (anchor is not null && layout.Plot.Width > 0)
        {
            ratio = Clamp((TimeToX(TimeSpan.FromSeconds(anchorTime)) - layout.Plot.Left) / layout.Plot.Width, 0, 1);
        }

        _zoom = target;
        var visible = _duration.TotalSeconds / _zoom;
        _viewStartSeconds = ClampViewStart(anchorTime - ratio * visible);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>恢复 1x 视口。</summary>
    public void ResetView()
    {
        if (Math.Abs(_zoom - MinZoom) < 0.0001 && _viewStartSeconds <= 0.0001)
        {
            return;
        }

        _zoom = MinZoom;
        _viewStartSeconds = 0;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>将视口定位到指定区间（用于「缩放至选区」）。</summary>
    public void FitRange(TimeSpan start, TimeSpan end)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var span = Math.Max(0.5, (end - start).TotalSeconds);
        _zoom = Clamp(_duration.TotalSeconds / span, MinZoom, MaxZoom);
        var visible = _duration.TotalSeconds / _zoom;
        _viewStartSeconds = ClampViewStart(start.TotalSeconds - (visible - span) / 2);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把控件内 X 坐标换算为时间。</summary>
    public TimeSpan XToTime(double x)
    {
        var layout = GetLayout();
        if (layout.Plot.Width <= 0)
        {
            return TimeSpan.Zero;
        }

        var ratio = (x - layout.Plot.Left) / layout.Plot.Width;
        return ClampTime(TimeSpan.FromSeconds(_viewStartSeconds + ratio * VisibleDuration.TotalSeconds));
    }

    /// <summary>把时间换算为控件内 X 坐标。</summary>
    public double TimeToX(TimeSpan time)
    {
        var layout = GetLayout();
        var visible = VisibleDuration.TotalSeconds;
        if (layout.Plot.Width <= 0 || visible <= 0)
        {
            return layout.Plot.Left;
        }

        return layout.Plot.Left + (time.TotalSeconds - _viewStartSeconds) / visible * layout.Plot.Width;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var layout = GetLayout();
        var pos = e.GetPosition(this);
        if (!layout.Plot.Contains(pos))
        {
            return;
        }

        var anchorTime = XToTime(pos.X);
        var factor = Math.Pow(1.3, e.Delta.Y);
        var zoom = Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001)
        {
            return;
        }

        var ratio = layout.Plot.Width > 0 ? Clamp((pos.X - layout.Plot.Left) / layout.Plot.Width, 0, 1) : 0;
        _zoom = zoom;
        var visible = _duration.TotalSeconds / _zoom;
        _viewStartSeconds = ClampViewStart(anchorTime.TotalSeconds - ratio * visible);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= TimeSpan.Zero || Bounds.Width < 40 || Bounds.Height < 60)
        {
            return;
        }

        var layout = GetLayout();
        var pos = e.GetPosition(this);
        if (!layout.Plot.Contains(pos))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _dragMode = DragMode.Pan;
            _panStartX = pos.X;
            _viewStartAtPanStart = _viewStartSeconds;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        var inX = TimeToX(_inPoint);
        var outX = TimeToX(_outPoint);
        var playheadX = TimeToX(_position);

        if (Math.Abs(pos.X - inX) <= HandleHitTolerancePx)
        {
            _dragMode = DragMode.In;
        }
        else if (Math.Abs(pos.X - outX) <= HandleHitTolerancePx)
        {
            _dragMode = DragMode.Out;
        }
        else if (TryHitHighlight(pos, layout, out var marker))
        {
            _position = ClampToSafe(ClampTime(marker!.Position));
            InvalidateVisual();
            SeekRequested?.Invoke(_position);
            e.Handled = true;
            return;
        }
        else if (Math.Abs(pos.X - playheadX) <= PlayheadHitTolerancePx)
        {
            _dragMode = DragMode.Playhead;
        }
        else
        {
            _dragMode = DragMode.Playhead;
            _position = ClampToSafe(XToTime(pos.X));
            InvalidateVisual();
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var layout = GetLayout();
        var pos = e.GetPosition(this);

        if (_dragMode == DragMode.None)
        {
            _hoverX = layout.Plot.Contains(pos) ? pos.X : null;
            InvalidateVisual();
            return;
        }

        switch (_dragMode)
        {
            case DragMode.Pan:
            {
                var delta = pos.X - _panStartX;
                if (layout.Plot.Width > 0)
                {
                    var visible = VisibleDuration.TotalSeconds;
                    var nextStart = _viewStartAtPanStart - delta / layout.Plot.Width * visible;
                    _viewStartSeconds = ClampViewStart(nextStart);
                    InvalidateVisual();
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
            }

            case DragMode.In:
            {
                var t = XToTime(pos.X);
                var maxIn = _outPoint.TotalSeconds - MinSelectionSeconds;
                t = TimeSpan.FromSeconds(Clamp(t.TotalSeconds, 0, Math.Max(0, maxIn)));
                t = ClampToSafe(t);
                if (t != _inPoint)
                {
                    _inPoint = t;
                    InvalidateVisual();
                    InOutChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
            }

            case DragMode.Out:
            {
                var t = XToTime(pos.X);
                var minOut = _inPoint.TotalSeconds + MinSelectionSeconds;
                var maxOut = Math.Max(minOut, _duration.TotalSeconds);
                if (_safeDuration > TimeSpan.Zero)
                {
                    maxOut = Math.Min(maxOut, Math.Max(minOut, _safeDuration.TotalSeconds));
                }

                t = TimeSpan.FromSeconds(Clamp(t.TotalSeconds, minOut, maxOut));
                if (t != _outPoint)
                {
                    _outPoint = t;
                    InvalidateVisual();
                    InOutChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
            }

            case DragMode.Playhead:
            {
                var t = ClampToSafe(XToTime(pos.X));
                if (t != _position)
                {
                    _position = t;
                    InvalidateVisual();
                }

                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragMode == DragMode.None)
        {
            return;
        }

        var mode = _dragMode;
        _dragMode = DragMode.None;
        e.Pointer.Capture(null);

        if (mode == DragMode.Playhead)
        {
            SeekRequested?.Invoke(_position);
        }

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragMode = DragMode.None;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragMode == DragMode.None && _hoverX is not null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        if (bounds.Width < 40 || bounds.Height < 60)
        {
            return;
        }

        var layout = GetLayout();
        context.FillRectangle(RulerBackgroundBrush, layout.Ruler);
        foreach (var row in new[] { layout.Wave, layout.Density, layout.Highlight })
        {
            context.FillRectangle(RowBackgroundBrush, row);
        }

        context.DrawRectangle(null, BorderPen, layout.Plot);

        if (_duration <= TimeSpan.Zero)
        {
            DrawText(context, "等待视频加载…", layout.Plot.Left + 12, layout.Ruler.Top + 4, 10, MutedTextBrush);
            return;
        }

        var rowsBottom = layout.Highlight.Bottom;
        Rect? playheadBubble;

        using (context.PushClip(new Rect(layout.Plot.Left, layout.Plot.Top, layout.Plot.Width, rowsBottom - layout.Plot.Top)))
        {
            DrawRuler(context, layout);
            DrawWaveform(context, layout);
            DrawDensity(context, layout);
            DrawHighlights(context, layout);
            DrawSelection(context, layout);
            DrawLiveEdgeMask(context, layout);
            playheadBubble = DrawPlayhead(context, layout);
            DrawHover(context, layout);
        }

        DrawZoomBadge(context, layout, playheadBubble);
    }

    private void DrawRuler(DrawingContext context, TimelineLayout layout)
    {
        var visible = VisibleDuration.TotalSeconds;
        var step = ChooseRulerStep(visible, layout.Plot.Width);
        var start = Math.Floor(_viewStartSeconds / step) * step;
        var end = _viewStartSeconds + visible;
        var withHours = _duration.TotalHours >= 1;

        for (var t = start; t <= end + 0.0001; t += step)
        {
            if (t < 0 || t > _duration.TotalSeconds + 0.0001)
            {
                continue;
            }

            var x = Math.Round(TimeToX(TimeSpan.FromSeconds(t))) + 0.5;
            if (x < layout.Plot.Left || x > layout.Plot.Right)
            {
                continue;
            }

            context.DrawLine(GridPen, new Point(x, layout.Ruler.Bottom), new Point(x, layout.Highlight.Bottom));

            var label = FormatRulerTime(t, withHours);
            var text = CreateText(label, 10, MutedTextBrush);
            var labelX = Math.Min(x + 3, layout.Plot.Right - text.Width - 2);
            context.DrawText(text, new Point(labelX, layout.Ruler.Top + 3));
        }
    }

    private void DrawWaveform(DrawingContext context, TimelineLayout layout)
    {
        var peaks = _waveform?.AudioPeaks;
        if (peaks is null || peaks.Length == 0)
        {
            DrawText(context, "波形提取中…", layout.Wave.Left + 12, layout.Wave.Top + layout.Wave.Height / 2 - 7, 10, MutedTextBrush);
            return;
        }

        var bucketSec = _duration.TotalSeconds / peaks.Length;
        if (bucketSec <= 0)
        {
            return;
        }

        var centerY = layout.Wave.Top + layout.Wave.Height / 2;
        var halfHeight = Math.Max(2, layout.Wave.Height / 2 - 3);
        var visible = VisibleDuration.TotalSeconds;
        var stepPx = visible / Math.Max(1, layout.Wave.Width);

        // 先按像素采样上下包络（开方压缩动态范围），再生成轻微圆角的波形柱。
        var maxPeak = 0.0;
        var barCount = Math.Max(1, (int)Math.Ceiling(layout.Wave.Width / BarStepPx));
        var heights = new double[barCount];
        for (var i = 0; i < barCount; i++)
        {
            var sec = _viewStartSeconds + (i + 0.5) * BarStepPx * stepPx;
            var peak = Math.Clamp(SampleEnvelope(peaks, sec, bucketSec), 0f, 1f);
            if (peak > maxPeak)
            {
                maxPeak = peak;
            }

            // 开方压缩动态范围：安静录像（峰值 ~0.01）也能看出起伏，响的段仍接近满幅。
            heights[i] = Math.Max(1.0, Math.Sqrt(peak) * halfHeight);
        }

        if (maxPeak <= 0.001)
        {
            DrawText(context, "（该视频没有可用的音频波形）", layout.Wave.Left + 12, layout.Wave.Top + layout.Wave.Height / 2 - 7, 10, MutedTextBrush);
            return;
        }

        var geometry = BuildWaveBars(layout, centerY, heights);
        // 中线先画，波形柱压在其上，只在柱间缝隙里透出一点基线。
        context.DrawLine(WaveGuidePen, new Point(layout.Plot.Left, centerY - 0.5), new Point(layout.Plot.Right, centerY - 0.5));

        var inX = TimeToX(_inPoint);
        var outX = TimeToX(_outPoint);
        var selLeft = Math.Max(layout.Wave.Left, Math.Min(inX, outX));
        var selRight = Math.Min(layout.Wave.Right, Math.Max(inX, outX));

        if (selRight - selLeft < 0.5)
        {
            // 无有效选区：整体保持 0.28 的暗形态。
            DrawWavePass(context, geometry, layout, layout.Wave.Left, layout.Wave.Right, WaveDimBrush);
        }
        else
        {
            // 聚光灯：选区外 alpha 0.28，选区内 alpha 0.72。
            DrawWavePass(context, geometry, layout, layout.Wave.Left, selLeft, WaveDimBrush);
            DrawWavePass(context, geometry, layout, selLeft, selRight, WaveBrightBrush);
            DrawWavePass(context, geometry, layout, selRight, layout.Wave.Right, WaveDimBrush);
        }
    }

    private void DrawDensity(DrawingContext context, TimelineLayout layout)
    {
        var peaks = _waveform?.AudioPeaks;
        var density = _waveform?.DanmakuDensity;
        var count = density?.Length ?? 0;
        if (peaks is not null && peaks.Length > 0 && count != peaks.Length)
        {
            count = Math.Min(count, peaks.Length);
        }

        if (density is null || count == 0)
        {
            return;
        }

        var bucketSec = _duration.TotalSeconds / density.Length;
        if (bucketSec <= 0)
        {
            return;
        }

        var baseline = layout.Density.Bottom - 1;
        var maxHeight = Math.Max(3, layout.Density.Height - 3);
        var visible = VisibleDuration.TotalSeconds;
        var width = (int)Math.Ceiling(layout.Density.Width);
        var stepPx = visible / Math.Max(1, layout.Density.Width);

        // 填充：顶部 #F59E0B 45% → 底部 0 的垂直渐变；描边只画顶部轮廓，避免整圈描边太“表格”。
        var fill = new StreamGeometry();
        var topProfile = new StreamGeometry();
        using (var g = fill.Open())
        using (var p = topProfile.Open())
        {
            g.BeginFigure(new Point(layout.Density.Left, baseline), true);
            var profileStarted = false;
            for (var i = 0; i < width; i++)
            {
                var sec = _viewStartSeconds + (i + 0.5) * stepPx;
                var value = SampleEnvelope(density, sec, bucketSec);
                var h = Math.Max(0, value * maxHeight);
                var px = layout.Density.Left + i + 0.5;
                var py = baseline - h;
                g.LineTo(new Point(px, py));

                if (!profileStarted)
                {
                    p.BeginFigure(new Point(px, py), false);
                    profileStarted = true;
                }
                else
                {
                    p.LineTo(new Point(px, py));
                }
            }

            g.LineTo(new Point(layout.Density.Right, baseline));
            g.EndFigure(true);

            if (profileStarted)
            {
                p.EndFigure(false);
            }
        }

        context.DrawGeometry(DensityFillBrush, null, fill);
        context.DrawGeometry(null, DensityOutlinePen, topProfile);
    }

    private void DrawHighlights(DrawingContext context, TimelineLayout layout)
    {
        var highlights = _highlights;
        if (highlights is null || highlights.Count == 0 || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var visible = VisibleDuration.TotalSeconds;
        if (visible <= 0)
        {
            return;
        }

        foreach (var marker in highlights)
        {
            if (marker.Position < TimeSpan.Zero || marker.Position > _duration)
            {
                continue;
            }

            var x = TimeToX(marker.Position);
            if (x < layout.Plot.Left - 8 || x > layout.Plot.Right + 8)
            {
                continue;
            }

            var half = Math.Max(3.0, layout.Plot.Width * 0.004);
            var rect = new Rect(x - half, layout.Highlight.Top + 1, half * 2, Math.Max(4, layout.Highlight.Height - 2));
            context.DrawRectangle(HighlightFillBrush, HighlightBorderPen, new RoundedRect(rect, 2));
        }
    }

    private void DrawSelection(DrawingContext context, TimelineLayout layout)
    {
        var inX = TimeToX(_inPoint);
        var outX = TimeToX(_outPoint);
        var top = layout.Wave.Top;
        var bottom = layout.Highlight.Bottom;

        if (inX > layout.Plot.Left)
        {
            context.FillRectangle(MaskBrush, new Rect(layout.Plot.Left, top, Math.Min(inX, layout.Plot.Right) - layout.Plot.Left, bottom - top));
        }

        if (outX < layout.Plot.Right)
        {
            var left = Math.Max(outX, layout.Plot.Left);
            context.FillRectangle(MaskBrush, new Rect(left, top, layout.Plot.Right - left, bottom - top));
        }

        if (outX > inX)
        {
            context.FillRectangle(SelectionFillBrush, new Rect(inX, top, outX - inX, bottom - top));
        }

        if (inX >= layout.Plot.Left - 1 && inX <= layout.Plot.Right + 1)
        {
            context.DrawLine(SelectionBorderPen, new Point(inX, top), new Point(inX, bottom));
            DrawInOutHandle(context, layout, inX);
            DrawText(context, "IN " + FormatClock(_inPoint), Clamp(inX + 6, layout.Plot.Left + 2, layout.Plot.Right - 64), top + 3, 9.5, HandleLabelBrush);
        }

        if (outX >= layout.Plot.Left - 1 && outX <= layout.Plot.Right + 1)
        {
            context.DrawLine(SelectionBorderPen, new Point(outX, top), new Point(outX, bottom));
            DrawInOutHandle(context, layout, outX);
            var text = CreateText("OUT " + FormatClock(_outPoint), 9.5, HandleLabelBrush);
            var textX = Math.Max(layout.Plot.Left + 2, Math.Min(outX - text.Width - 6, layout.Plot.Right - text.Width - 2));
            context.DrawText(text, new Point(textX, top + 3));
        }
    }

    /// <summary>
    /// 边录边剪：在 [SafeDuration, Duration) 的热区绘制极淡灰遮罩 + 45° 斜纹，
    /// 明确“这之后的录制内容尚未安全可剪”。SafeDuration 为 0 或已覆盖全片时不绘制。
    /// </summary>
    private void DrawLiveEdgeMask(DrawingContext context, TimelineLayout layout)
    {
        if (_safeDuration <= TimeSpan.Zero || _duration <= TimeSpan.Zero || _safeDuration >= _duration)
        {
            return;
        }

        var safeX = TimeToX(_safeDuration);
        if (safeX >= layout.Plot.Right)
        {
            return;
        }

        var left = Math.Max(layout.Plot.Left, safeX);
        var top = layout.Wave.Top;
        var bottom = layout.Highlight.Bottom;
        var width = layout.Plot.Right - left;
        var height = bottom - top;
        if (width <= 0.5 || height <= 0.5)
        {
            return;
        }

        var area = new Rect(left, top, width, height);
        context.FillRectangle(LiveEdgeMaskBrush, area);

        // 45° 细斜纹：间距约 8px，只画在遮罩范围内；用 clip 防止溢出到刻度/圆角外。
        const double stripeSpacing = 8.0;
        using (context.PushClip(area))
        {
            for (var offset = 0.0; offset <= width + height; offset += stripeSpacing)
            {
                var startX = left + offset;
                context.DrawLine(
                    LiveEdgeStripePen,
                    new Point(startX, top),
                    new Point(startX - height, bottom));
            }
        }
    }

    /// <summary>绘制播放头与顶部时间码气泡，返回气泡矩形供缩放提示避让。</summary>
    private Rect? DrawPlayhead(DrawingContext context, TimelineLayout layout)
    {
        if (_position < TimeSpan.Zero)
        {
            return null;
        }

        var x = Math.Round(TimeToX(_position)) + 0.5;
        if (x < layout.Plot.Left || x > layout.Plot.Right)
        {
            return null;
        }

        context.DrawLine(PlayheadPen, new Point(x, layout.Ruler.Bottom), new Point(x, layout.Highlight.Bottom));

        // 播放头顶部的小时间码气泡：水平位置始终夹在时间轴左右边界内。
        var label = FormatClock(_position);
        var text = CreateText(label, 9.5, PlayheadBubbleTextBrush);
        var bubbleWidth = text.Width + 10;
        var bubbleHeight = 15.0;
        var bubbleLeft = Clamp(
            x - bubbleWidth / 2,
            layout.Plot.Left + 2,
            Math.Max(layout.Plot.Left + 2, layout.Plot.Right - bubbleWidth - 2));
        var bubbleTop = layout.Ruler.Top + 1;
        var bubbleRect = new Rect(bubbleLeft, bubbleTop, bubbleWidth, bubbleHeight);
        var bubble = new RoundedRect(bubbleRect, 3);
        context.DrawRectangle(PlayheadBrush, null, bubble);
        context.DrawText(text, new Point(bubbleLeft + 5, bubbleTop + (bubbleHeight - text.Height) / 2));

        // 气泡与播放头竖线之间的三角指针。
        var pointer = new StreamGeometry();
        using (var g = pointer.Open())
        {
            g.BeginFigure(new Point(x - 4, bubbleTop + bubbleHeight), true);
            g.LineTo(new Point(x + 4, bubbleTop + bubbleHeight));
            g.LineTo(new Point(x, layout.Ruler.Bottom));
            g.EndFigure(true);
        }

        context.DrawGeometry(PlayheadBrush, null, pointer);
        return bubbleRect;
    }

    private void DrawHover(DrawingContext context, TimelineLayout layout)
    {
        if (_hoverX is not { } x || _dragMode != DragMode.None)
        {
            return;
        }

        if (x < layout.Plot.Left || x > layout.Plot.Right)
        {
            return;
        }

        var snapped = Math.Round(x) + 0.5;
        context.DrawLine(HoverPen, new Point(snapped, layout.Ruler.Bottom), new Point(snapped, layout.Highlight.Bottom));
        var time = XToTime(x);
        var label = FormatClock(time);
        var text = CreateText(label, 9.5, TextBrush);
        var textX = Clamp(snapped + 4, layout.Plot.Left + 2, layout.Plot.Right - text.Width - 2);
        // 注意：绘制被裁剪在 Highlight.Bottom 以内，悬停时间码放在高能行底部内侧才可见。
        var textY = layout.Highlight.Bottom - text.Height - 3;
        context.DrawText(text, new Point(textX, textY));
    }

    /// <summary>按列构建轻微圆角的波形柱几何体（一次构建，按选区裁剪分层绘制）。</summary>
    private static StreamGeometry BuildWaveBars(TimelineLayout layout, double centerY, double[] heights)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < heights.Length; i++)
            {
                var x = layout.Wave.Left + i * BarStepPx;
                var barWidth = Math.Min(BarWidthPx, layout.Wave.Right - x);
                if (barWidth <= 0.5)
                {
                    break;
                }

                var h = heights[i];
                AppendRoundedBar(g, x, centerY - h, barWidth, h * 2, BarRadiusPx);
            }
        }

        return geometry;
    }

    /// <summary>向几何体追加一根轻微圆角的竖条（上下各 r 圆角）。</summary>
    private static void AppendRoundedBar(StreamGeometryContext g, double x, double y, double width, double height, double radius)
    {
        if (width <= 0.01 || height <= 0.01)
        {
            return;
        }

        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var x0 = x;
        var x1 = x + width;
        var y0 = y;
        var y1 = y + height;
        var corner = new Size(r, r);

        g.BeginFigure(new Point(x0 + r, y0), true);
        g.LineTo(new Point(x1 - r, y0));
        g.ArcTo(new Point(x1, y0 + r), corner, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(x1, y1 - r));
        g.ArcTo(new Point(x1 - r, y1), corner, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(x0 + r, y1));
        g.ArcTo(new Point(x0, y1 - r), corner, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(x0, y0 + r));
        g.ArcTo(new Point(x0 + r, y0), corner, 0, false, SweepDirection.Clockwise);
        g.EndFigure(true);
    }

    /// <summary>在指定 X 区间内绘制一次波形：选区外 alpha 0.28、选区内 alpha 0.72（聚光灯）。</summary>
    private static void DrawWavePass(DrawingContext context, StreamGeometry geometry, TimelineLayout layout, double left, double right, IBrush brush)
    {
        if (right - left < 0.5)
        {
            return;
        }

        using (context.PushClip(new Rect(left, layout.Wave.Top - 1, right - left, layout.Wave.Height + 2)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }

    /// <summary>入点/出点手柄：圆角竖条 + 顶部小横杠 + 中心高光（替代旧三角）。</summary>
    private static void DrawInOutHandle(DrawingContext context, TimelineLayout layout, double x)
    {
        var top = layout.Wave.Top;
        var bottom = layout.Highlight.Bottom;

        // 圆角竖条：3px 宽、半径 1.5。
        context.DrawRectangle(SelectionBorderBrush, null, new RoundedRect(new Rect(x - 1.5, top, 3.0, bottom - top), 1.5));

        // 顶部小横杠：11px 宽、3px 高。
        context.DrawRectangle(SelectionBorderBrush, null, new RoundedRect(new Rect(x - 5.5, top - 3.0, 11.0, 3.0), 1.5));

        // 手柄中心高光，让手柄在暗背景上更有立体感。
        context.DrawRectangle(HandleLabelBrush, null, new RoundedRect(new Rect(x - 0.75, top + 2, 1.5, 5), 0.75));
    }

    private void DrawZoomBadge(DrawingContext context, TimelineLayout layout, Rect? playheadBubble)
    {
        var label = _zoom <= MinZoom + 0.001 ? "1.0x · 滚轮缩放，双击复位" : $"{_zoom:0.0}x · 右键拖动平移";
        var text = CreateText(label, 9.5, MutedTextBrush);
        var rect = new Rect(layout.Plot.Right - text.Width - 8, layout.Ruler.Top + 1, text.Width + 6, text.Height + 2);
        // 播放头气泡滑到右端时，把缩放提示临时挪到左端，避免两段文字叠在一起。
        if (playheadBubble is { } bubble && bubble.Intersects(rect))
        {
            rect = rect.WithX(layout.Plot.Left + 6);
        }

        context.FillRectangle(ZoomBadgeBrush, rect);
        context.DrawText(text, new Point(rect.X + 3, rect.Y + 1));
    }

    private bool TryHitHighlight(Point pos, TimelineLayout layout, out ClipHighlightMarker? marker)
    {
        marker = null;
        if (_highlights is null || _highlights.Count == 0)
        {
            return false;
        }

        if (pos.Y < layout.Highlight.Top - 4 || pos.Y > layout.Highlight.Bottom + 4)
        {
            return false;
        }

        foreach (var item in _highlights)
        {
            if (Math.Abs(TimeToX(item.Position) - pos.X) <= 8)
            {
                marker = item;
                return true;
            }
        }

        return false;
    }

    private double SampleEnvelope(float[] data, double seconds, double bucketSec)
    {
        if (data.Length == 0 || bucketSec <= 0)
        {
            return 0;
        }

        var index = seconds / bucketSec;
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
        var a = data[i0];
        var b = data[i0 + 1];
        return a + (b - a) * frac;
    }

    private static double ChooseRulerStep(double visibleSeconds, double widthPx)
    {
        const double minLabelPx = 82;
        foreach (var step in RulerStepsSeconds)
        {
            if (widthPx <= 0)
            {
                return step;
            }

            if (step / Math.Max(0.0001, visibleSeconds) * widthPx >= minLabelPx)
            {
                return step;
            }
        }

        return RulerStepsSeconds[^1];
    }

    private TimelineLayout GetLayout()
    {
        var width = Math.Max(1, Bounds.Width - LeftPadding - RightPadding);
        var contentTop = TopPadding + RulerHeight + RowGap;
        var contentHeight = Math.Max(24, Bounds.Height - contentTop - BottomPadding - RowGap * 2);
        var waveHeight = contentHeight * 0.5;
        var densityHeight = contentHeight * 0.25;
        var highlightHeight = Math.Max(10, contentHeight - waveHeight - densityHeight);
        var rect = new Rect(LeftPadding, TopPadding, width, Math.Max(24, Bounds.Height - TopPadding - BottomPadding));
        var ruler = new Rect(LeftPadding, TopPadding, width, RulerHeight);
        var wave = new Rect(LeftPadding, contentTop, width, waveHeight);
        var density = new Rect(LeftPadding, wave.Bottom + RowGap, width, densityHeight);
        var highlight = new Rect(LeftPadding, density.Bottom + RowGap, width, highlightHeight);
        return new TimelineLayout(rect, ruler, wave, density, highlight);
    }

    private double DisplayedCenterSeconds()
    {
        return _viewStartSeconds + VisibleDuration.TotalSeconds / 2;
    }

    private double ClampViewStart(double start)
    {
        var visible = _duration.TotalSeconds / Math.Max(MinZoom, _zoom);
        var maxStart = Math.Max(0, _duration.TotalSeconds - visible);
        if (maxStart <= 0)
        {
            return 0;
        }

        return Clamp(start, 0, maxStart);
    }

    private TimeSpan ClampTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && time > _duration)
        {
            return _duration;
        }

        return time;
    }

    /// <summary>边录边剪时把播放头/选区限制在安全可剪截止时间内；未启用（Zero）时原样返回。</summary>
    private TimeSpan ClampToSafe(TimeSpan time)
    {
        if (_safeDuration > TimeSpan.Zero && time > _safeDuration)
        {
            return _safeDuration;
        }

        return time;
    }

    private void ClampState()
    {
        if (_duration <= TimeSpan.Zero)
        {
            _position = TimeSpan.Zero;
            _inPoint = TimeSpan.Zero;
            _outPoint = TimeSpan.Zero;
            _viewStartSeconds = 0;
            _zoom = MinZoom;
            return;
        }

        if (_outPoint <= TimeSpan.Zero || _outPoint > _duration)
        {
            _outPoint = _duration;
        }

        if (_inPoint < TimeSpan.Zero || _inPoint > _outPoint)
        {
            _inPoint = TimeSpan.Zero;
        }

        if (_outPoint.TotalSeconds - _inPoint.TotalSeconds < MinSelectionSeconds)
        {
            if (_inPoint.TotalSeconds + MinSelectionSeconds <= _duration.TotalSeconds)
            {
                _outPoint = TimeSpan.FromSeconds(_inPoint.TotalSeconds + MinSelectionSeconds);
            }
            else
            {
                _inPoint = TimeSpan.FromSeconds(Math.Max(0, _duration.TotalSeconds - MinSelectionSeconds));
                _outPoint = _duration;
            }
        }

        _position = ClampTime(_position);
        _viewStartSeconds = ClampViewStart(_viewStartSeconds);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    private FormattedText CreateText(string text, double size, IBrush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            size,
            brush);
    }

    private void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush brush)
    {
        context.DrawText(CreateText(text, size, brush), new Point(x, y));
    }

    private static string FormatRulerTime(double seconds, bool withHours)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (withHours)
        {
            return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static string FormatClock(TimeSpan time)
    {
        var ts = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }

        return $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    private readonly record struct TimelineLayout(
        Rect Plot,
        Rect Ruler,
        Rect Wave,
        Rect Density,
        Rect Highlight);
}
