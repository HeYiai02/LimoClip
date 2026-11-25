using System.Diagnostics;
using System.Globalization;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>「边录边剪」增量波形提取：只解码请求区间，按固定每秒桶数返回可拼接的峰值数组。</summary>
public sealed class LiveWaveformDeltaExtractorTests : IDisposable
{
    private static readonly string? FfmpegPath;

    private readonly string _workDir;

    static LiveWaveformDeltaExtractorTests()
    {
        FfmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    public LiveWaveformDeltaExtractorTests()
    {
        _workDir = Path.Combine(
            Path.GetTempPath(),
            "limoclip-live-waveform-tests",
            Guid.NewGuid().ToString("N"));
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
    public async Task Extracts_each_requested_range_in_fixed_buckets()
    {
        if (FfmpegPath is null)
        {
            return; // 本机缺少 ffmpeg 时跳过。
        }

        var video = GenerateVideo("live.mp4", withAudio: true, seconds: 2);
        var extractor = new LiveWaveformDeltaExtractor(FfmpegPath);

        var first = await extractor.ExtractPeaksAsync(
            video, TimeSpan.Zero, TimeSpan.FromSeconds(1), peaksPerSecond: 2);
        var second = await extractor.ExtractPeaksAsync(
            video, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), peaksPerSecond: 2);

        // 桶数只由请求区间决定（与文件后续增长无关），调用方按每秒固定分辨率拼接。
        Assert.Equal(2, first.Length);
        Assert.Equal(2, second.Length);
        Assert.All(first, peak => Assert.InRange(peak, 0f, 1f));
        Assert.All(second, peak => Assert.InRange(peak, 0f, 1f));
        // ffmpeg lavfi sine 默认幅度约 -18dBFS，解码后峰值约 0.12；非静音即证明逐桶峰值生效。
        Assert.True(first.Max() > 0.05f, $"首段应有非静音峰值，实际 max={first.Max()}。");
        Assert.True(second.Max() > 0.05f, $"第二段应有非静音峰值，实际 max={second.Max()}。");
    }

    [Fact]
    public async Task Clamps_peaks_per_second_and_returns_empty_for_invalid_range()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("range.mp4", withAudio: true, seconds: 1);
        var extractor = new LiveWaveformDeltaExtractor(FfmpegPath);

        // peaksPerSecond 下限 1。
        var clamped = await extractor.ExtractPeaksAsync(
            video, TimeSpan.Zero, TimeSpan.FromSeconds(1), peaksPerSecond: 0);
        Assert.Single(clamped);

        Assert.Empty(await extractor.ExtractPeaksAsync(
            video, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Empty(await extractor.ExtractPeaksAsync(
            video, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public async Task Throws_file_not_found_for_missing_video_or_ffmpeg()
    {
        var extractor = new LiveWaveformDeltaExtractor(
            FfmpegPath ?? Path.Combine(_workDir, "ffmpeg.exe"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => extractor.ExtractPeaksAsync(
            Path.Combine(_workDir, "nope.mkv"), TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("plain.mp4", withAudio: true, seconds: 1);
        var missingFfmpeg = new LiveWaveformDeltaExtractor(Path.Combine(_workDir, "no-ffmpeg.exe"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => missingFfmpeg.ExtractPeaksAsync(
            video, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Video_without_audio_reports_extraction_failure()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("silent.mp4", withAudio: false, seconds: 1);
        var extractor = new LiveWaveformDeltaExtractor(FfmpegPath);

        // 无音轨/尾部尚不可解码时 ffmpeg 不产出采样：上层按“本次无可提取数据”降级处理。
        await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractPeaksAsync(
            video, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
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
