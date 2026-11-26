using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>
/// 音量表纯逻辑（V0.7.5 抽离）的单元测试：astats filter 转义、RMS 电平解析、
/// dB → 0~1 映射与表针平滑。
/// </summary>
public sealed class AudioMeterLogicTests
{
    [Fact]
    public void BuildMeterFilter_EscapesWindowsDriveLetter()
    {
        var filter = AudioMeterLogic.BuildMeterFilter(@"E:\temp\meter-abc.txt");

        Assert.Equal(
            @"lavfi=[astats=metadata=1:reset=5,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=E\\:/temp/meter-abc.txt:direct=1]",
            filter);
    }

    [Fact]
    public void BuildMeterFilter_PortablePathWithoutDriveLetter_IsOnlySlashNormalized()
    {
        var filter = AudioMeterLogic.BuildMeterFilter(@"/tmp/audio-meter/meter.txt");

        Assert.Equal(
            @"lavfi=[astats=metadata=1:reset=5,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=/tmp/audio-meter/meter.txt:direct=1]",
            filter);
    }

    [Fact]
    public void BuildMeterFilter_UsesRmsKeyWithoutTrailingEquals()
    {
        var filter = AudioMeterLogic.BuildMeterFilter(@"C:\a.txt");

        Assert.Contains("key=lavfi.astats.Overall.RMS_level:file=", filter);
        Assert.DoesNotContain("RMS_level=", filter);
    }

    [Fact]
    public void FindLastRmsDecibels_ReturnsLastOccurrence()
    {
        var text =
            "frame:0    pts:0\n" +
            "lavfi.astats.Overall.RMS_level=-31.5\n" +
            "frame:1    pts:1024\n" +
            "lavfi.astats.Overall.RMS_level=-12.25\n";

        Assert.Equal(-12.25, AudioMeterLogic.FindLastRmsDecibels(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("frame:0 pts:0")]
    [InlineData("lavfi.astats.Overall.RMS_level=not-a-number")]
    [InlineData("lavfi.astats.Overall.RMS_level=NaN")]
    [InlineData("lavfi.astats.Overall.RMS_level=Infinity")]
    public void FindLastRmsDecibels_MissingOrInvalid_ReturnsNull(string? text)
    {
        Assert.Null(AudioMeterLogic.FindLastRmsDecibels(text));
    }

    [Fact]
    public void FindLastRmsDecibels_ValueAtEndOfText_ParsesWithoutNewline()
    {
        Assert.Equal(-6d, AudioMeterLogic.FindLastRmsDecibels("lavfi.astats.Overall.RMS_level=-6"));
    }

    [Fact]
    public void FindLastRmsDecibels_ValueFollowedByCarriageReturn_StopsAtLineBreak()
    {
        var text = "lavfi.astats.Overall.RMS_level=-9.5\r\nnext line";

        Assert.Equal(-9.5, AudioMeterLogic.FindLastRmsDecibels(text));
    }

    [Theory]
    [InlineData(-120d, 0d)]
    [InlineData(-60d, 0d)]
    [InlineData(-30d, 0.5d)]
    [InlineData(-15d, 0.75d)]
    [InlineData(0d, 1d)]
    [InlineData(6d, 1d)]
    public void MapDecibelsToLevel_ClampsToUnitRange(double decibels, double expected)
    {
        Assert.Equal(expected, AudioMeterLogic.MapDecibelsToLevel(decibels), precision: 10);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MapDecibelsToLevel_NonFinite_ReturnsZero(double decibels)
    {
        Assert.Equal(0d, AudioMeterLogic.MapDecibelsToLevel(decibels));
    }

    [Fact]
    public void NextLevel_RiseFollowsTargetImmediately()
    {
        Assert.Equal(0.8d, AudioMeterLogic.NextLevel(0.2d, 0.8d));
    }

    [Fact]
    public void NextLevel_FallUsesExponentialSmoothing()
    {
        // 0.8 * 0.72 + 0.2 * 0.28 = 0.632
        Assert.Equal(0.632d, AudioMeterLogic.NextLevel(0.8d, 0.2d), precision: 10);
    }

    [Fact]
    public void NextLevel_EqualValues_StayUnchanged()
    {
        Assert.Equal(0.5d, AudioMeterLogic.NextLevel(0.5d, 0.5d));
    }

    [Fact]
    public void Constants_MatchMpvFilterContract()
    {
        Assert.Equal(-60d, AudioMeterLogic.MinimumDecibels);
        Assert.Equal(8192, AudioMeterLogic.MaxTailBytes);
        Assert.Contains("lavfi.astats.Overall.RMS_level=", AudioMeterLogic.RmsKey);
    }
}
