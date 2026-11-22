using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 控制栏里的 LED 音频电平表：16 根竖条，按当前 RMS 电平点亮。
/// 只负责渲染，数据由 MpvPlayerView 的后台读数定时喂入。
/// 绿色（安全）→ 琥珀（偏高）→ 红色（接近削波），和常见调音台电平表一致。
/// </summary>
public sealed class AudioLevelMeter : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<AudioLevelMeter, double>(nameof(Level));

    private static readonly IBrush LowBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#34D399"));
    private static readonly IBrush MidBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FBBF24"));
    private static readonly IBrush HighBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F87171"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#273244"));

    static AudioLevelMeter()
    {
        AffectsRender<AudioLevelMeter>(LevelProperty);
    }

    /// <summary>0 = 静音，1 = 满电平。</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        const int barCount = 16;
        const double gap = 1.2;
        var barWidth = Math.Max(1, (width - gap * (barCount - 1)) / barCount);
        var level = Math.Clamp(Level, 0, 1);
        var litBars = (int)Math.Round(level * barCount);

        for (var i = 0; i < barCount; i++)
        {
            var x = i * (barWidth + gap);
            var rect = new Rect(x, 0, barWidth, height);
            var brush = i < barCount * 0.55
                ? LowBrush
                : i < barCount * 0.82 ? MidBrush : HighBrush;
            context.FillRectangle(i < litBars ? brush : IdleBrush, rect);
        }
    }
}
