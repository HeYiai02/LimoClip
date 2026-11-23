using System.Diagnostics;
using System.Globalization;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class ClipExporterTests : IDisposable
{
    private static readonly string? FfmpegPath;

    private readonly string _workDir;
    private readonly string _outputDir;

    static ClipExporterTests()
    {
        FfmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    public ClipExporterTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "limoclip-exporter-tests", Guid.NewGuid().ToString("N"));
        _outputDir = Path.Combine(_workDir, "out");
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
    public async Task Clean_export_produces_short_playable_mp4()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("clean.mp4", seconds: 4, withAudio: true);
        using var guard = new ProcessJobGuard();
        var exporter = CreateExporter(guard);

        var outputs = await exporter.ExportAsync(
            BuildRequest(video, "clean-clip", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), ClipExportMode.CleanOnly),
            danmakuItems: null,
            sourceVideoCodec: "mpeg4");

        Assert.Single(outputs);
        var output = outputs[0];
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);

        var metadata = await new VideoProbeService(FfmpegPath).ProbeAsync(output);
        Assert.Equal(160, metadata.Width);
        Assert.Equal(120, metadata.Height);
        Assert.InRange(metadata.Duration.TotalSeconds, 1.0, 3.5); // -ss 关键帧对齐误差
        Assert.Equal("mpeg4", metadata.VideoCodec);
    }

    [Fact]
    public async Task Both_mode_produces_clean_and_burned_versions()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("both.mp4", seconds: 3, withAudio: true);
        var jsonl = WriteDanmakuFile("both.jsonl");
        using var guard = new ProcessJobGuard();
        var exporter = CreateExporter(guard);
        var progressReports = new List<ClipExportProgress>();
        var progress = new Progress<ClipExportProgress>(progressReports.Add);

        var outputs = await exporter.ExportAsync(
            BuildRequest(video, "both-clip", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.5), ClipExportMode.Both),
            danmakuItems: await DanmakuJsonlReader.ReadAllAsync(jsonl),
            sourceVideoCodec: "mpeg4",
            progress: progress);

        Assert.Equal(2, outputs.Count);
        Assert.True(outputs[0].EndsWith("_clean.mp4", StringComparison.Ordinal));
        Assert.True(outputs[1].EndsWith("_danmaku.mp4", StringComparison.Ordinal));
        foreach (var output in outputs)
        {
            Assert.True(File.Exists(output));
            var metadata = await new VideoProbeService(FfmpegPath).ProbeAsync(output);
            Assert.InRange(metadata.Duration.TotalSeconds, 1.5, 3.0);
        }

        Assert.NotEmpty(progressReports);
        Assert.Equal(100, progressReports[^1].Percentage);
    }

    [Fact]
    public async Task Burned_export_without_danmaku_still_succeeds()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("nodm.mp4", seconds: 2, withAudio: true);
        var exporter = CreateExporter(null);

        var outputs = await exporter.ExportAsync(
            BuildRequest(video, "nodm-clip", TimeSpan.Zero, TimeSpan.FromSeconds(1.5), ClipExportMode.BurnedDanmaku),
            danmakuItems: null,
            sourceVideoCodec: "mpeg4");

        Assert.Single(outputs);
        Assert.True(File.Exists(outputs[0]));
    }

    [Fact]
    public async Task Cancellation_kills_ffmpeg_and_removes_partial_output()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var video = GenerateVideo("cancel.mp4", seconds: 8, withAudio: true, width: 640, height: 360, fps: 30);
        var exporter = CreateExporter(null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var task = exporter.ExportAsync(
            BuildRequest(video, "cancel-clip", TimeSpan.Zero, TimeSpan.FromSeconds(6), ClipExportMode.BurnedDanmaku),
            danmakuItems: null,
            sourceVideoCodec: "mpeg4",
            progress: null,
            ct: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(_outputDir, "cancel-clip*.mp4"));
    }

    [Fact]
    public async Task Missing_input_throws_file_not_found()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var exporter = CreateExporter(null);
        var request = BuildRequest(
            Path.Combine(_workDir, "missing.mp4"), "missing-clip",
            TimeSpan.Zero, TimeSpan.FromSeconds(1), ClipExportMode.CleanOnly);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            exporter.ExportAsync(request, null, "mpeg4"));
    }

    [Fact]
    public async Task Rejects_end_not_after_start()
    {
        if (FfmpegPath is null)
        {
            return;
        }

        var exporter = CreateExporter(null);
        var request = BuildRequest(
            Path.Combine(_workDir, "whatever.mp4"), "invalid-clip",
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), ClipExportMode.CleanOnly);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            exporter.ExportAsync(request, null, "mpeg4"));
    }

    private ClipExporter CreateExporter(ProcessJobGuard? guard)
    {
        var detector = new HardwareEncoderDetector(FfmpegPath!);
        var converter = new DanmakuAssConverter();
        return new ClipExporter(FfmpegPath!, guard, detector, converter);
    }

    private ClipExportRequest BuildRequest(
        string inputPath,
        string baseName,
        TimeSpan start,
        TimeSpan end,
        ClipExportMode mode)
        => new(
            InputVideoPath: inputPath,
            CompanionDanmakuPath: null,
            OutputDirectory: _outputDir,
            OutputBaseName: baseName,
            StartTime: start,
            EndTime: end,
            Mode: mode);

    private string WriteDanmakuFile(string fileName)
    {
        var path = Path.Combine(_workDir, fileName);
        File.WriteAllLines(path, new[]
        {
            "{\"type\":\"meta\",\"version\":1}",
            "{\"type\":\"dm\",\"offsetMs\":0,\"uname\":\"a\",\"text\":\"第一发\"}",
            "{\"type\":\"dm\",\"offsetMs\":600,\"uname\":\"b\",\"text\":\"第二发\"}",
            "{\"type\":\"dm\",\"offsetMs\":1400,\"uname\":\"c\",\"text\":\"第三发\"}",
            "{\"type\":\"dm\",\"offsetMs\":2400,\"uname\":\"d\",\"text\":\"第四发\"}",
        });
        return path;
    }

    private string GenerateVideo(
        string fileName,
        double seconds,
        bool withAudio,
        int width = 160,
        int height = 120,
        int fps = 10)
    {
        var path = Path.Combine(_workDir, fileName);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=duration={duration}:size={width}x{height}:rate={fps}",
        };
        if (withAudio)
        {
            arguments.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}" });
        }

        arguments.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "5", "-g", (fps * 1).ToString(CultureInfo.InvariantCulture) });
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
