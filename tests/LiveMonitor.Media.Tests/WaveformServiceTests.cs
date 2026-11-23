using System.Diagnostics;
using System.Globalization;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class WaveformServiceTests : IDisposable
{
    private static readonly string? FfmpegPath;

    private readonly string _workDir;
    private readonly string _cacheDir;

    static WaveformServiceTests()
    {
        FfmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    public WaveformServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "limoclip-waveform-tests", Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_workDir, "cache");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch
        {
            // 测试目录清理尽力而为。
        }
    }

    [Fact]
    public async Task Extracts_audio_peaks_in_2000_buckets()
    {
        if (FfmpegPath is null)
        {
            return; // 本机缺少 ffmpeg 时跳过。
        }

        var video = GenerateVideo("audio.mp4", withAudio: true);
        var service = new WaveformService(FfmpegPath, _cacheDir);

        var waveform = await service.GetWaveformAsync(video, TimeSpan.FromSeconds(2));

        Assert.Equal(2000, waveform.AudioPeaks.Length);
        Assert.Equal(2000, waveform.DanmakuDensity.Length);
        Assert.All(waveform.AudioPeaks, peak => Assert.InRange(peak, 0f, 1f));
        // ffmpeg lavfi sine 默认幅度约 -18dBFS，解码后峰值约 0.12；只要非静音即证明逐桶峰值生效。
        Assert.True(
            waveform.AudioPeaks.Max() > 0.1f,
            $"440Hz 正弦应产生明显峰值，实际 max={waveform.AudioPeaks.Max()}，非零桶={waveform.AudioPeaks.Count(p => p > 0)}。");
        Assert.Equal(0, waveform.MaxDanmakuPerBucket);
        Assert.All(waveform.DanmakuDensity, density => Assert.Equal(0f, density));
    }

    [Fact]
    public async Task Second_call_hits_cache_without_running_ffmpeg()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("cache.mp4", withAudio: true);
        var first = await new WaveformService(FfmpegPath, _cacheDir)
            .GetWaveformAsync(video, TimeSpan.FromSeconds(2));

        Assert.Single(Directory.GetFiles(_cacheDir, "*.wf.json"));

        // 假 ffmpeg 路径证明命中缓存时不会再启动进程。
        var missingFfmpeg = Path.Combine(_workDir, "no-ffmpeg.exe");
        var second = await new WaveformService(missingFfmpeg, _cacheDir)
            .GetWaveformAsync(video, TimeSpan.FromSeconds(2));

        Assert.Equal(first.Duration, second.Duration);
        Assert.Equal(first.AudioPeaks, second.AudioPeaks);
        Assert.Equal(first.DanmakuDensity, second.DanmakuDensity);
        Assert.Equal(first.MaxDanmakuPerBucket, second.MaxDanmakuPerBucket);
    }

    [Fact]
    public async Task Builds_normalized_danmaku_density_from_jsonl()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("density.mp4", withAudio: true);
        var densityJsonl = Path.Combine(_workDir, "density.jsonl");
        await File.WriteAllLinesAsync(densityJsonl, new[]
        {
            "{\"type\":\"meta\",\"version\":1}",
            "{\"type\":\"dm\",\"offsetMs\":0,\"uname\":\"a\",\"text\":\"x\"}",
            "{\"type\":\"dm\",\"offsetMs\":50,\"uname\":\"b\",\"text\":\"y\"}",
            "{\"type\":\"dm\",\"offsetMs\":1000,\"uname\":\"c\",\"text\":\"z\"}",
            "{\"type\":\"dm\",\"offsetMs\":1900,\"uname\":\"d\",\"text\":\"w\"}",
        });

        var waveform = await new WaveformService(FfmpegPath, _cacheDir)
            .GetWaveformAsync(video, TimeSpan.FromSeconds(2), densityJsonl, bucketCount: 20);

        Assert.Equal(2, waveform.MaxDanmakuPerBucket);
        Assert.Equal(1.0f, waveform.DanmakuDensity[0]); // 0ms 与 50ms 同落第 0 桶（100ms/桶）
        Assert.Equal(0.5f, waveform.DanmakuDensity[10]); // 1000ms
        Assert.Equal(0.5f, waveform.DanmakuDensity[19]); // 1900ms
        Assert.Equal(2.0f, waveform.DanmakuDensity.Sum());
    }

    [Fact]
    public async Task Missing_danmaku_file_yields_zero_density()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("nodm.mp4", withAudio: true);
        var waveform = await new WaveformService(FfmpegPath, _cacheDir)
            .GetWaveformAsync(video, TimeSpan.FromSeconds(2), Path.Combine(_workDir, "missing.jsonl"), bucketCount: 10);

        Assert.Equal(0, waveform.MaxDanmakuPerBucket);
        Assert.All(waveform.DanmakuDensity, density => Assert.Equal(0f, density));
    }

    [Fact]
    public async Task Video_without_audio_yields_zero_peaks()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("silent.mp4", withAudio: false, seconds: 1);
        var waveform = await new WaveformService(FfmpegPath, _cacheDir)
            .GetWaveformAsync(video, TimeSpan.FromSeconds(1), bucketCount: 100);

        Assert.Equal(100, waveform.AudioPeaks.Length);
        Assert.All(waveform.AudioPeaks, peak => Assert.Equal(0f, peak));
    }

    [Fact]
    public async Task Missing_video_throws_file_not_found()
    {
        var service = new WaveformService(Path.Combine(_workDir, "no-ffmpeg.exe"), _cacheDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.GetWaveformAsync(Path.Combine(_workDir, "nope.mkv"), TimeSpan.FromSeconds(1)));
    }

    private string GenerateVideo(string fileName, bool withAudio, double seconds = 2)
    {
        var path = Path.Combine(_workDir, fileName);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=duration={duration}:size=160x120:rate=10",
        };
        if (withAudio)
        {
            arguments.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}" });
        }

        arguments.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "5" });
        if (withAudio)
        {
            arguments.AddRange(new[] { "-c:a", "aac", "-shortest" });
        }

        arguments.Add(path);
        RunFfmpeg(arguments);
        return path;
    }

    private static void RunFfmpeg(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(FfmpegPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"ffmpeg 生成素材失败 exit={process.ExitCode}：{stderr}{stdout}");
    }
}
