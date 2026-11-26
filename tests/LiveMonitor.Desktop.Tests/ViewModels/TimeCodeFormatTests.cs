using System;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class TimeCodeFormatTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(86399, "23:59:59")]
    public void Clock_FormatsTimeSpan(int seconds, string expected)
        => Assert.Equal(expected, TimeCodeFormat.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Clock_NegativeIsZero()
        => Assert.Equal("00:00", TimeCodeFormat.Clock(TimeSpan.FromSeconds(-5)));

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(65.7, "01:05")]
    [InlineData(3600.0, "1:00:00")]
    public void Clock_FormatsSeconds(double seconds, string expected)
        => Assert.Equal(expected, TimeCodeFormat.Clock(seconds));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-3.5)]
    public void Clock_InvalidSecondsIsZero(double seconds)
        => Assert.Equal("00:00", TimeCodeFormat.Clock(seconds));

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(1, "0:01.0")]
    [InlineData(59, "0:59.0")]
    [InlineData(90, "1:30.0")]
    [InlineData(3600, "1:00:00.0")]
    [InlineData(3661, "1:01:01.0")]
    public void ClockWithTenths_UsesMinuteAndTenths(int seconds, string expected)
        => Assert.Equal(expected, TimeCodeFormat.ClockWithTenths(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ClockWithTenths_KeepsSubsecond()
        => Assert.Equal("0:01.2", TimeCodeFormat.ClockWithTenths(TimeSpan.FromMilliseconds(1250)));

    [Fact]
    public void ClockWithTenths_NegativeIsZero()
        => Assert.Equal("0:00.0", TimeCodeFormat.ClockWithTenths(TimeSpan.FromMilliseconds(-200)));

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(60, "01:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3661, "01:01:01")]
    [InlineData(36000, "10:00:00")]
    public void DurationZeroPadded_PadsHours(int seconds, string expected)
        => Assert.Equal(expected, TimeCodeFormat.DurationZeroPadded(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DurationZeroPadded_NegativeIsZero()
        => Assert.Equal("00:00", TimeCodeFormat.DurationZeroPadded(TimeSpan.FromSeconds(-1)));

    [Theory]
    [InlineData(0, "00.00")]
    [InlineData(5, "00.05")]
    [InlineData(65, "01.05")]
    [InlineData(3600, "1.00.00")]
    [InlineData(3665, "1.01.05")]
    public void FileSafeClock_UsesDots(int seconds, string expected)
        => Assert.Equal(expected, TimeCodeFormat.FileSafeClock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void BusinessFormats_DelegateToTimeCodeFormat()
    {
        var span = TimeSpan.FromSeconds(3665);
        Assert.Equal(TimeCodeFormat.Clock(span), ClipListDisplayLogic.FormatTime(span));
        Assert.Equal(TimeCodeFormat.Clock(3665), ReplaySeekLogic.FormatClock(3665));
        Assert.Equal(TimeCodeFormat.DurationZeroPadded(span), RecordingUiFormat.FormatDuration(span));
        Assert.Equal(TimeCodeFormat.FileSafeClock(span), ClipExportNamingLogic.FormatClock(span));
        Assert.Equal("01:01:05", RecordingUiFormat.FormatDuration(3665));
    }

    [Fact]
    public void ClipExportNaming_BuildDefaultOutputName_UsesFileSafeClock()
    {
        var name = ClipExportNamingLogic.BuildDefaultOutputName("/tmp/room.mkv", TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(3600));
        Assert.Equal("room_01.05-1.00.00", name);
    }
}
