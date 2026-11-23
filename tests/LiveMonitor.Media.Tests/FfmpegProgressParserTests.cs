using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class FfmpegProgressParserTests
{
    [Theory]
    [InlineData("total_size=1048576", null, 1048576L)]
    [InlineData("  total_size=42  ", null, 42L)]
    public void TryParseLine_TotalSize(string line, string? _, long expected)
    {
        Assert.True(FfmpegProgressParser.TryParseLine(line, out var update));
        Assert.Equal(expected, update.TotalSizeBytes);
        Assert.Null(update.OutTimeMicroseconds);
    }

    [Theory]
    [InlineData("out_time_us=1500000", 1_500_000L)]
    [InlineData("out_time_ms=2500000", 2_500_000L)]
    [InlineData("out_time_us=N/A", null)]
    public void TryParseLine_OutTime(string line, long? expected)
    {
        var parsed = FfmpegProgressParser.TryParseLine(line, out var update);

        if (expected is null)
        {
            Assert.False(parsed);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(expected, update.OutTimeMicroseconds);
    }

    [Fact]
    public void TryParseLine_ClockOutTime_ConvertsToMicroseconds()
    {
        Assert.True(FfmpegProgressParser.TryParseLine("out_time=00:00:01.500000", out var update));
        Assert.Equal(1_500_000L, update.OutTimeMicroseconds);
    }

    [Theory]
    [InlineData("bitrate=1234.5kbits/s", 1234.5)]
    [InlineData("bitrate=0.0kbits/s", 0.0)]
    public void TryParseLine_Bitrate(string line, double expected)
    {
        Assert.True(FfmpegProgressParser.TryParseLine(line, out var update));
        Assert.Equal(expected, update.BitrateKbps);
    }

    [Fact]
    public void TryParseLine_NaBitrate_ReturnsFalse()
    {
        Assert.False(FfmpegProgressParser.TryParseLine("bitrate=N/A", out _));
    }

    [Theory]
    [InlineData("progress=continue", false)]
    [InlineData("progress=end", true)]
    public void TryParseLine_ProgressFlag(string line, bool isEnd)
    {
        Assert.True(FfmpegProgressParser.TryParseLine(line, out var update));
        Assert.Equal(isEnd, update.IsEnd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("frame=12")]
    [InlineData("no-separator")]
    [InlineData("=missing-key")]
    public void TryParseLine_UnknownOrInvalid_ReturnsFalse(string line)
    {
        Assert.False(FfmpegProgressParser.TryParseLine(line, out _));
    }

    [Fact]
    public void TryParseLine_Null_ReturnsFalse()
    {
        Assert.False(FfmpegProgressParser.TryParseLine(null, out _));
    }

    [Fact]
    public void TryParseClockDuration_ParsesClockText()
    {
        Assert.True(FfmpegProgressParser.TryParseClockDuration("01:02:03.250000", out var duration));
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 250), duration);
        Assert.False(FfmpegProgressParser.TryParseClockDuration("N/A", out _));
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("abc")]
    public void TryParseBitrate_InvalidText_ReturnsFalse(string value)
    {
        Assert.False(FfmpegProgressParser.TryParseBitrateKbps(value, out _));
    }
}
