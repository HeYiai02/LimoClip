using System;
using System.IO;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipExportNamingLogicTests
{
    [Fact]
    public void BuildDefaultOutputName_UsesBaseNameAndSelectionRange()
    {
        var name = ClipExportNamingLogic.BuildDefaultOutputName(
            @"C:\videos\20260101_stream_1.mkv", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65));

        Assert.Equal("20260101_stream_1_00.05-01.05", name);
    }

    [Fact]
    public void BuildDefaultOutputName_UsesHourFormatWhenOverOneHour()
    {
        var name = ClipExportNamingLogic.BuildDefaultOutputName(
            @"clip.mp4", TimeSpan.FromSeconds(3600), TimeSpan.FromSeconds(3665));

        Assert.Equal("clip_1.00.00-1.01.05", name);
    }

    [Fact]
    public void BuildDefaultOutputName_TruncatesToMaxLength()
    {
        var longBase = new string('a', 120);
        var name = ClipExportNamingLogic.BuildDefaultOutputName(
            $@"C:\videos\{longBase}.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.Equal(ClipExportNamingLogic.MaxOutputNameLength, name.Length);
        Assert.StartsWith(new string('a', 90), name);
    }

    [Theory]
    [InlineData(0, "00.00")]
    [InlineData(5, "00.05")]
    [InlineData(65, "01.05")]
    [InlineData(3599, "59.59")]
    [InlineData(3600, "1.00.00")]
    [InlineData(3665, "1.01.05")]
    [InlineData(86399, "23.59.59")]
    public void FormatClock_UsesMinutesBelowOneHourAndHoursAbove(int totalSeconds, string expected)
    {
        Assert.Equal(expected, ClipExportNamingLogic.FormatClock(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void SanitizeFileName_KeepsNormalName()
    {
        Assert.Equal("my clip", ClipExportNamingLogic.SanitizeFileName("  my clip  ", DateTimeOffset.MinValue));
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var raw = "a<b>c:d\"e/f\\g|h?i*j";

        var sanitized = ClipExportNamingLogic.SanitizeFileName(raw, DateTimeOffset.MinValue);

        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), ch => sanitized.Contains(ch));
        Assert.Contains("_", sanitized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeFileName_EmptyInput_FallsBackToTimestamp(string? raw)
    {
        var now = new DateTimeOffset(2026, 11, 26, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal("clip_20261126_183000", ClipExportNamingLogic.SanitizeFileName(raw, now));
    }

    [Fact]
    public void SanitizeFileName_AllInvalidCharacters_BecomeUnderscores()
    {
        var raw = new string(Path.GetInvalidFileNameChars()[0], 3);

        Assert.Equal("___", ClipExportNamingLogic.SanitizeFileName(raw, DateTimeOffset.MinValue));
    }

    [Fact]
    public void MaxOutputNameLength_IsNinety()
    {
        Assert.Equal(90, ClipExportNamingLogic.MaxOutputNameLength);
    }
}
