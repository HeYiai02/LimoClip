using System.Diagnostics;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// <see cref="FfmpegSegmentMerger"/> 的直接单测：覆盖四条现有真实行为——
/// 不存在路径被过滤、单分片走 File.Move、多分片真实 concat 成功后清理分片、
/// concat 非零退出时保留分片并返回错误。需要 ffmpeg 的用例在本地缺失时跳过，
/// 但在 CI（CI=true）中必须显式失败，避免整批用例静默跳过。
/// </summary>
public sealed class SegmentMergerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests",
        "SegmentMerger",
        Guid.NewGuid().ToString("N"));

    private string? _ffmpegPath;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        if (FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            _ffmpegPath = ffmpegPath;
            return;
        }

        // CI 守卫：CI 环境缺少 ffmpeg 时不要静默跳过，直接失败暴露环境缺依赖。
        Assert.False(
            IsCiEnvironment(),
            $"CI 环境必须能定位到 ffmpeg（FfmpegLocator.RelativePath），当前工作目录：{Environment.CurrentDirectory}");
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
    public async Task MergeAsync_FiltersMissingAndBlankSegments()
    {
        var merger = new FfmpegSegmentMerger(Path.Combine(_directory, "missing-ffmpeg.exe"));
        var segments = new[]
        {
            new RecordedSegment(Path.Combine(_directory, "gone-1.mkv"), TimeSpan.FromSeconds(1), null),
            new RecordedSegment("   ", TimeSpan.FromSeconds(1), null),
            new RecordedSegment(Path.Combine(_directory, "gone-2.mkv"), TimeSpan.Zero, null),
        };

        var result = await merger.MergeAsync(Path.Combine(_directory, "merged.mkv"), segments);

        Assert.False(result.Merged);
        Assert.Equal(0, result.FileSizeBytes);
        Assert.Equal("没有可用的录制分片。", result.ErrorMessage);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.False(File.Exists(Path.Combine(_directory, "merged.mkv")));
    }

    [Fact]
    public async Task MergeAsync_SingleSegment_MovesFileToOutput()
    {
        var directory = Path.Combine(_directory, "single");
        Directory.CreateDirectory(directory);
        var partPath = Path.Combine(directory, "part-000.mkv");
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(partPath, payload);

        // 单分片路径不会调用 ffmpeg，用不存在的可执行文件即可。
        var merger = new FfmpegSegmentMerger(Path.Combine(_directory, "missing-ffmpeg.exe"));
        var outputPath = Path.Combine(directory, "nested", "merged.mkv");
        var duration = TimeSpan.FromSeconds(1.25);

        var result = await merger.MergeAsync(
            outputPath,
            new[] { new RecordedSegment(partPath, duration, null) });

        Assert.True(result.Merged);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(duration, result.Duration);
        Assert.Equal(payload.Length, result.FileSizeBytes);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(outputPath));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public async Task MergeAsync_MultipleSegments_ConcatenatesAndDeletesParts()
    {
        if (_ffmpegPath is null)
        {
            return; // 本机缺少 ffmpeg 时跳过；CI 缺 ffmpeg 会在 InitializeAsync 中失败。
        }

        var directory = Path.Combine(_directory, "concat");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "part-000.mkv");
        var secondPath = Path.Combine(directory, "part-001.mkv");
        await CreateTestVideoAsync(firstPath);
        await CreateTestVideoAsync(secondPath);

        var merger = new FfmpegSegmentMerger(_ffmpegPath);
        var outputPath = Path.Combine(directory, "merged.mkv");

        var result = await merger.MergeAsync(
            outputPath,
            new[]
            {
                new RecordedSegment(firstPath, TimeSpan.FromSeconds(1), null),
                new RecordedSegment(secondPath, TimeSpan.FromSeconds(1), null),
            });

        Assert.True(result.Merged, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
        Assert.True(File.Exists(outputPath));
        Assert.True(result.FileSizeBytes > 0);
        Assert.Equal(new FileInfo(outputPath).Length, result.FileSizeBytes);
        // 两段各 1 秒的视频，合并后内容时长应接近 2 秒（允许 ffmpeg 测量的少量偏差）。
        Assert.InRange(result.Duration.TotalSeconds, 1.8, 2.2);
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(secondPath));
        Assert.False(File.Exists(outputPath + ".concat.txt"));
        // 合并产物必须是 ffmpeg 可解码的 matroska 文件。
        Assert.Equal(0, await RunFfmpegAsync(new[] { "-v", "error", "-i", outputPath, "-f", "null", "-" }));
    }

    [Fact]
    public async Task MergeAsync_ConcatFailure_KeepsSegmentsAndReportsError()
    {
        if (_ffmpegPath is null)
        {
            return; // 本机缺少 ffmpeg 时跳过；CI 缺 ffmpeg 会在 InitializeAsync 中失败。
        }

        var directory = Path.Combine(_directory, "failure");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "part-000.mkv");
        var secondPath = Path.Combine(directory, "part-001.mkv");
        // 非法媒体内容：时长测量失败保留原时长，concat 阶段 ffmpeg 非零退出。
        await File.WriteAllBytesAsync(firstPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        await File.WriteAllBytesAsync(secondPath, new byte[] { 0x04, 0x05, 0x06, 0x07 });

        var merger = new FfmpegSegmentMerger(_ffmpegPath);
        var outputPath = Path.Combine(directory, "merged.mkv");

        var result = await merger.MergeAsync(
            outputPath,
            new[]
            {
                new RecordedSegment(firstPath, TimeSpan.FromSeconds(1), null),
                new RecordedSegment(secondPath, TimeSpan.FromSeconds(1), null),
            });

        Assert.False(result.Merged);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("ffmpeg 合并分片失败（exit=", result.ErrorMessage);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(new FileInfo(firstPath).Length + new FileInfo(secondPath).Length, result.FileSizeBytes);
        Assert.True(File.Exists(firstPath), "合并失败时第一个分片应保留。");
        Assert.True(File.Exists(secondPath), "合并失败时第二个分片应保留。");
        Assert.False(File.Exists(outputPath + ".concat.txt"));
    }

    private async Task CreateTestVideoAsync(string path)
    {
        var exitCode = await RunFfmpegAsync(
            new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=1:size=160x120:rate=10",
                "-c:v", "mpeg4", "-q:v", "5",
                path,
            });
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(path));
    }

    private async Task<int> RunFfmpegAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        _ = await stderrTask;
        return process.ExitCode;
    }

    private static bool IsCiEnvironment()
        => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}
