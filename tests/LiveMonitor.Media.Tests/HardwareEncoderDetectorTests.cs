using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class HardwareEncoderDetectorTests
{
    private static readonly string? FfmpegPath;

    static HardwareEncoderDetectorTests()
    {
        FfmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    [Fact]
    public async Task Detects_an_encoder_with_ffmpeg()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var detector = new HardwareEncoderDetector(FfmpegPath);
        var (name, extraArgs) = await detector.DetectBestAsync();

        Assert.Contains(name, new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" });
        Assert.NotEmpty(extraArgs);
        Assert.NotNull(detector.DetectionLog);
    }

    [Fact]
    public async Task Second_call_returns_same_encoder()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var detector = new HardwareEncoderDetector(FfmpegPath);
        var first = await detector.DetectBestAsync();
        var second = await detector.DetectBestAsync();

        Assert.Equal(first.EncoderName, second.EncoderName);
        Assert.Equal(first.ExtraArgs, second.ExtraArgs);
    }

    [Fact]
    public async Task Falls_back_to_libx264_when_ffmpeg_missing()
    {
        var detector = new HardwareEncoderDetector(Path.Combine(Path.GetTempPath(), "missing-ffmpeg-detector.exe"));

        var (name, extraArgs) = await detector.DetectBestAsync();

        Assert.Equal("libx264", name);
        Assert.NotEmpty(extraArgs);
    }
}
