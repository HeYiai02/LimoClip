using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心两个展示 VM 共用的数值/状态格式化，避免时长、大小、状态配色各写一份。
/// </summary>
internal static class RecordingUiFormat
{
    public static string PlatformBadge(PlatformType platform) => PlatformBadgeFormat.Text(platform);

    public static string PlatformBadgeBackground(PlatformType platform) => PlatformBadgeFormat.Background(platform);

    public static string PlatformBadgeForeground(PlatformType platform) => PlatformBadgeFormat.Foreground(platform);

    public static string PlatformName(PlatformType platform) => PlatformBadgeFormat.DisplayName(platform);

    public static string FormatDuration(TimeSpan duration) => TimeCodeFormat.DurationZeroPadded(duration);

    public static string FormatDuration(double seconds)
        => FormatDuration(TimeSpan.FromSeconds(Math.Max(0d, seconds)));

    public static string FormatBitrate(double kbps)
        => kbps <= 0d ? "--" : $"{kbps:0} kbps";

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024d;
        if (kb < 1024)
        {
            return $"{kb:0.0} KB";
        }

        var mb = kb / 1024d;
        return mb >= 1024 ? $"{mb / 1024d:0.00} GB" : $"{mb:0.0} MB";
    }

    public static string FormatStartedAt(DateTime value)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Local => value,
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
        };
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public static string StatusText(RecordingStatus status) => status switch
    {
        RecordingStatus.Starting => "启动中",
        RecordingStatus.Recording => "录制中",
        RecordingStatus.Stopping => "停止中",
        RecordingStatus.Completed => "已完成",
        RecordingStatus.Failed => "失败",
        _ => "未知"
    };

    public static string StatusBrush(RecordingStatus status) => status switch
    {
        RecordingStatus.Starting or RecordingStatus.Stopping => "#FBBF24",
        RecordingStatus.Recording => "#F87171",
        RecordingStatus.Completed => "#3DDC97",
        RecordingStatus.Failed => "#F87171",
        _ => "#5B6472"
    };
}
