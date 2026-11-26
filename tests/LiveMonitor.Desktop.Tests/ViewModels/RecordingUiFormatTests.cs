using System;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>录制中心共用格式化（平台徽标/时长/大小/状态配色）的直接单测。</summary>
public sealed class RecordingUiFormatTests
{
    [Theory]
    [InlineData(PlatformType.Bilibili, "B站")]
    [InlineData(PlatformType.Douyin, "抖音")]
    [InlineData(PlatformType.Douyu, "斗鱼")]
    [InlineData(PlatformType.Huya, "虎牙")]
    [InlineData(PlatformType.Twitch, "Twitch")]
    [InlineData(PlatformType.YouTube, "YouTube")]
    [InlineData(PlatformType.CustomStream, "清流")]
    public void PlatformBadge_MapsKnownPlatforms(PlatformType platform, string expected)
        => Assert.Equal(expected, RecordingUiFormat.PlatformBadge(platform));

    [Fact]
    public void PlatformBadge_UnknownPlatform_UsesDash()
        => Assert.Equal("—", RecordingUiFormat.PlatformBadge((PlatformType)42));

    [Theory]
    [InlineData(PlatformType.Bilibili, "#FB7299")]
    [InlineData(PlatformType.Twitch, "#9146FF")]
    [InlineData(PlatformType.Douyin, "#FE2C55")]
    [InlineData(PlatformType.Douyu, "#FF6A00")]
    [InlineData(PlatformType.Huya, "#FFB300")]
    [InlineData(PlatformType.YouTube, "#FF0033")]
    [InlineData(PlatformType.CustomStream, "#0EA5E9")]
    public void PlatformBadgeBackground_MapsKnownPlatforms(PlatformType platform, string expected)
        => Assert.Equal(expected, RecordingUiFormat.PlatformBadgeBackground(platform));

    [Fact]
    public void PlatformBadgeBackground_UnknownPlatform_UsesDefaultBrush()
        => Assert.Equal("#2A3140", RecordingUiFormat.PlatformBadgeBackground((PlatformType)42));

    [Fact]
    public void PlatformBadgeForeground_OnlyHuyaUsesDarkText()
    {
        Assert.Equal("#1A1A1A", RecordingUiFormat.PlatformBadgeForeground(PlatformType.Huya));
        Assert.Equal("#FFFFFF", RecordingUiFormat.PlatformBadgeForeground(PlatformType.Bilibili));
        Assert.Equal("#FFFFFF", RecordingUiFormat.PlatformBadgeForeground((PlatformType)42));
    }

    [Theory]
    [InlineData(PlatformType.Bilibili, "哔哩哔哩")]
    [InlineData(PlatformType.Douyin, "抖音")]
    [InlineData(PlatformType.Douyu, "斗鱼")]
    [InlineData(PlatformType.Huya, "虎牙")]
    [InlineData(PlatformType.Twitch, "Twitch")]
    [InlineData(PlatformType.YouTube, "YouTube")]
    [InlineData(PlatformType.CustomStream, "自定义清流")]
    public void PlatformName_MapsKnownPlatforms(PlatformType platform, string expected)
        => Assert.Equal(expected, RecordingUiFormat.PlatformName(platform));

    [Fact]
    public void PlatformName_UnknownPlatform_FallsBackToOther()
        => Assert.Equal("其他平台", RecordingUiFormat.PlatformName((PlatformType)42));

    [Theory]
    [InlineData(-5, "00:00")]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3661, "01:01:01")]
    [InlineData(36000, "10:00:00")]
    public void FormatDuration_TimeSpan_UsesMinutesOrHourFormat(int totalSeconds, string expected)
        => Assert.Equal(expected, RecordingUiFormat.FormatDuration(TimeSpan.FromSeconds(totalSeconds)));

    [Theory]
    [InlineData(-12.0, "00:00")]
    [InlineData(0.0, "00:00")]
    [InlineData(90.0, "01:30")]
    [InlineData(3725.0, "01:02:05")]
    public void FormatDuration_Double_ClampsNegativeAndMatchesTimeSpan(double seconds, string expected)
        => Assert.Equal(expected, RecordingUiFormat.FormatDuration(seconds));

    [Theory]
    [InlineData(0.0, "--")]
    [InlineData(-1.0, "--")]
    [InlineData(2500.0, "2500 kbps")]
    [InlineData(999.9, "1000 kbps")]
    public void FormatBitrate_HandlesNonPositiveAndRounds(double kbps, string expected)
        => Assert.Equal(expected, RecordingUiFormat.FormatBitrate(kbps));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(-5L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(1610612736L, "1.50 GB")]
    public void FormatBytes_UsesBinaryStepsAndTwoDecimalGigabytes(long bytes, string expected)
        => Assert.Equal(expected, RecordingUiFormat.FormatBytes(bytes));

    [Fact]
    public void FormatStartedAt_ConvertsUtcToLocalAndUsesSecondPrecision()
    {
        var utc = new DateTime(2026, 11, 26, 8, 5, 9, DateTimeKind.Utc);
        var expected = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(expected, RecordingUiFormat.FormatStartedAt(utc));

        var local = new DateTime(2026, 11, 26, 8, 5, 9, DateTimeKind.Local);
        Assert.Equal("2026-11-26 08:05:09", RecordingUiFormat.FormatStartedAt(local));
    }

    [Fact]
    public void FormatStartedAt_UnspecifiedKind_IsTreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 26, 8, 5, 9, DateTimeKind.Unspecified);
        var expected = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(expected, RecordingUiFormat.FormatStartedAt(unspecified));
    }

    [Theory]
    [InlineData(RecordingStatus.Starting, "启动中")]
    [InlineData(RecordingStatus.Recording, "录制中")]
    [InlineData(RecordingStatus.Stopping, "停止中")]
    [InlineData(RecordingStatus.Completed, "已完成")]
    [InlineData(RecordingStatus.Failed, "失败")]
    public void StatusText_MapsAllStatuses(RecordingStatus status, string expected)
        => Assert.Equal(expected, RecordingUiFormat.StatusText(status));

    [Fact]
    public void StatusText_UnknownStatus_FallsBack()
        => Assert.Equal("未知", RecordingUiFormat.StatusText((RecordingStatus)99));

    [Theory]
    [InlineData(RecordingStatus.Starting, "#FBBF24")]
    [InlineData(RecordingStatus.Recording, "#F87171")]
    [InlineData(RecordingStatus.Stopping, "#FBBF24")]
    [InlineData(RecordingStatus.Completed, "#3DDC97")]
    [InlineData(RecordingStatus.Failed, "#F87171")]
    public void StatusBrush_MapsAllStatuses(RecordingStatus status, string expected)
        => Assert.Equal(expected, RecordingUiFormat.StatusBrush(status));

    [Fact]
    public void StatusBrush_UnknownStatus_FallsBackToMuted()
        => Assert.Equal("#5B6472", RecordingUiFormat.StatusBrush((RecordingStatus)99));
}
