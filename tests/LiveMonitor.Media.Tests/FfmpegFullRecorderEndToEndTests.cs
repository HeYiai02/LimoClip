using System.Diagnostics;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// 用本机打包的 ffmpeg 做一次真实录制闭环：合成源 → -c copy 录制 .mkv → 用 ffmpeg 再读校验，
/// 覆盖 FfmpegFullRecorder 的锚点、进度、退出上报与文件可读性（不依赖网络）。
/// </summary>
public sealed class FfmpegFullRecorderEndToEndTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LiveMonitor.Media.Tests", Guid.NewGuid().ToString("N"));
    private string? _ffmpegPath;
    private string? _sourcePath;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        _ffmpegPath = ffmpegPath;
        _sourcePath = Path.Combine(_directory, "source.mp4");

        var exitCode = await RunFfmpegAsync(
            new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=1.5:size=160x120:rate=10",
                "-c:v", "mpeg4", "-q:v", "5",
                _sourcePath
            });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(_sourcePath));
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
    public async Task RecordsLocalSource_ToSeekableMkv()
    {
        if (_ffmpegPath is null || _sourcePath is null)
        {
            return; // 本机无 ffmpeg 时跳过（打包环境由 scripts/fetch-tools.ps1 -WithFfmpeg 提供）。
        }

        var outputPath = Path.Combine(_directory, "recorded.mkv");
        var quality = new StreamQuality(
            Id: "local",
            DisplayName: "本地合成源",
            Height: 120,
            Fps: 10,
            StreamUrl: _sourcePath,
            RequiredHeaders: new Dictionary<string, string>());

        var anchorTcs = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedTcs = new TaskCompletionSource<(int ExitCode, string? Stderr)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressCount = 0;

        var recorder = new FfmpegFullRecorder(_ffmpegPath);
        recorder.OnFirstFrameAnchored += anchor => anchorTcs.TrySetResult(anchor);
        recorder.OnProgressUpdated += (_, _, _) => Interlocked.Increment(ref progressCount);
        recorder.OnExited += (exitCode, stderr) => exitedTcs.TrySetResult((exitCode, stderr));

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            await recorder.StartAsync(quality, outputPath);

            var anchor = await anchorTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var (exitCode, _) = await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
            Assert.True(anchor >= startedAt.AddSeconds(-30), $"锚点时间过早：{anchor:O}，启动时间：{startedAt:O}");
            Assert.True(anchor <= DateTimeOffset.UtcNow.AddSeconds(30), $"锚点时间过晚：{anchor:O}");
            Assert.True(Volatile.Read(ref progressCount) > 0, "未收到任何进度事件。");
        }
        finally
        {
            await recorder.DisposeAsync();
        }

        var verifyExitCode = await RunFfmpegAsync(new[] { "-y", "-v", "error", "-i", outputPath, "-f", "null", "-" });
        Assert.Equal(0, verifyExitCode);
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderrTask;
        return process.ExitCode;
    }
}
