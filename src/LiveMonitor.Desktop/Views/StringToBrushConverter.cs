using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LiveMonitor.Desktop.Views;

/// <summary>
/// 把 ViewModel 中的颜色字符串（如 "#F87171"）转换为画刷，供录制中心 XAML 绑定背景/前景色。
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return new SolidColorBrush(Avalonia.Media.Color.Parse(text.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
