using System;
using System.Globalization;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 界面时间码 / 时长格式化的统一入口。不同区域的既有文案格式并不相同
/// （是否补零、分隔符、是否带十分位），这里按“显式变体”保留各自语义，
/// 避免合并成单一格式而改变界面文案；各业务格式类通过委托复用同一实现。
/// </summary>
internal static class TimeCodeFormat
{
    /// <summary>秒级时间码：≥1 小时用 <c>h:mm:ss</c>，否则 <c>mm:ss</c>；负值按 0。</summary>
    public static string Clock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>秒数时间码：非有限值或负数按 0，再走 <see cref="Clock(TimeSpan)"/>。</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        return Clock(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>时间轴标尺 / 播放头时间码：带十分位（<c>h:mm:ss.t</c> / <c>m:ss.t</c>）。</summary>
    public static string ClockWithTenths(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var tenths = time.Milliseconds / 100;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{tenths}"
            : $"{time.Minutes}:{time.Seconds:00}.{tenths}";
    }

    /// <summary>录制时长：≥1 小时 <c>HH:mm:ss</c>（小时两位补零），否则 <c>mm:ss</c>；负值按 0。</summary>
    public static string DurationZeroPadded(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var totalHours = (int)duration.TotalHours;
        return totalHours > 0
            ? $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    /// <summary>文件名安全时间码：点分隔 <c>h.mm.ss</c> / <c>mm.ss</c>。</summary>
    public static string FileSafeClock(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\.mm\.ss", CultureInfo.InvariantCulture)
        : time.ToString(@"mm\.ss", CultureInfo.InvariantCulture);
}
