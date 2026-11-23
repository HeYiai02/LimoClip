using System.Diagnostics;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class VideoProbeServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests.Probe",
        Guid.NewGuid().ToString("N"));

    private string? _ffmpegPath;
    private string? _sourcePath;

    private const string H264ProbeOutput = """
        ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers
        Input #0, matroska,webm, from 'E:\cache\recordings\20261123_044406_#1_sample.mkv':
          Metadata:
            DISPLAYWIDTH    : 1280
            DISPLAYHEIGHT   : 720
            FPS             : 30
          Duration: 00:00:37.78, start: 0.000000, bitrate: 1742 kb/s
          Stream #0:0: Video: h264 (Main), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 30 fps, 30 tbr, 1k tbn, start 0.0
          Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp
        At least one output file must be specified
        """;

    private const string HevcProbeOutput = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'E:\cache\recordings\hevc.mp4':
          Duration: 01:12:05.36, start: 0.000000, bitrate: 6123 kb/s
          Stream #0:0(und): Video: hevc (Main 10) (hev1 / 0x31657668), yuv420p10le(tv, bt709), 1920x1080, 59.94 fps, 59.94 tbr, 90k tbn
          Stream #0:1(und): Audio: aac (LC) (mp4a / 0x6134706d), 48000 Hz, stereo, fltp, 192 kb/s
        At least one output file must be specified
        """;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        _ffmpegPath = ffmpegPath;
        _sourcePath = Path.Combine(_directory, "source.mp4");
        var exitCode = await RunFfmpegAsync(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=160x120:rate=10",
            "-c:v", "mpeg4", "-q:v", "5",
            _sourcePath
        });
        Assert.Equal(0, exitCode);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void TryParseProbeOutput_ParsesMatroskaH264()
    {
        Assert.True(VideoProbeService.TryParseProbeOutput(H264ProbeOutput, out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(37780, (long)metadata!.Duration.TotalMilliseconds);
        Assert.Equal(1280, metadata.Width);
        Assert.Equal(720, metadata.Height);
        Assert.Equal(30, metadata.Fps, precision: 3);
        Assert.Equal("h264", metadata.VideoCodec);
    }

    [Fact]
    public void TryParseProbeOutput_ParsesHevcWithFractionalFps()
    {
        Assert.True(VideoProbeService.TryParseProbeOutput(HevcProbeOutput, out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(1920, metadata!.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(59.94, metadata.Fps, precision: 2);
        Assert.Equal("hevc", metadata.VideoCodec);
        Assert.Equal(new TimeSpan(1, 12, 5) + TimeSpan.FromMilliseconds(360), metadata.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Input #0, matroska,webm\n  Duration: N/A\n")]
    [InlineData("Input #0, matroska,webm\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: aac (LC)\n")]
    public void TryParseProbeOutput_ReturnsFalseForUnusableOutput(string stderr)
    {
        Assert.False(VideoProbeService.TryParseProbeOutput(stderr, out _));
    }

    [Fact]
    public async Task ProbeAsync_ReadsSyntheticMp4()
    {
        if (_ffmpegPath is null || _sourcePath is null)
        {
            return; // 本机无 ffmpeg 时跳过。
        }

        var service = new VideoProbeService(_ffmpegPath);
        var metadata = await service.ProbeAsync(_sourcePath);

        Assert.Equal(160, metadata.Width);
        Assert.Equal(120, metadata.Height);
        Assert.Equal(10, metadata.Fps, precision: 3);
        Assert.Equal("mpeg4", metadata.VideoCodec);
        Assert.InRange(metadata.Duration.TotalSeconds, 0.9, 1.5);
    }

    [Fact]
    public async Task ProbeAsync_ThrowsForMissingFile()
    {
        var service = new VideoProbeService(_ffmpegPath ?? Path.Combine(_directory, "ffmpeg.exe"));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ProbeAsync(Path.Combine(_directory, "missing.mp4")));
    }

    private async Task<int> RunFfmpegAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
