using System.Diagnostics;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// FfmpegFullRecorder 的进程级测试：用真实 ffmpeg 指向不可达地址，验证进程退出/事件/锚点兜底/Dispose 幂等。
/// 找不到 tools/ffmpeg/ffmpeg.exe 时跳过（返回而不是失败）。
/// </summary>
public sealed class FfmpegFullRecorderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "LiveMonitor.Media.Tests", Guid.NewGuid().ToString("N"));

    private readonly string? _ffmpegPath;

    public FfmpegFullRecorderTests()
    {
        Directory.CreateDirectory(_directory);
        _ffmpegPath = FfmpegLocator.TryLocate(out var path) ? path : null;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void FfmpegLocator_FindsBundledExecutable()
    {
        Assert.True(
            _ffmpegPath is not null,
            $"未找到 ffmpeg，请确认 {FfmpegLocator.RelativePath} 存在（scripts/fetch-tools.ps1 -WithFfmpeg）");
    }

    [Fact]
    public async Task StartAsync_UnreachableStream_ExitsAndAnchorsWithFallback()
    {
        if (_ffmpegPath is null)
        {
            return;
        }

        var outputPath = Path.Combine(_directory, "failed.mkv");
        var recorder = new FfmpegFullRecorder(_ffmpegPath);

        var anchored = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<(int ExitCode, string? Stderr)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.OnFirstFrameAnchored += anchor => anchored.TrySetResult(anchor);
        recorder.OnExited += (exitCode, stderr) => exited.TrySetResult((exitCode, stderr));

        await recorder.StartAsync(
            new StreamQuality(
                Id: "test",
                DisplayName: "不可达流",
                Height: 720,
                Fps: 30,
                StreamUrl: "http://127.0.0.1:1/live.flv",
                RequiredHeaders: new Dictionary<string, string>
                {
                    ["User-Agent"] = "LiveMonitor.Tests/1.0",
                    ["Referer"] = "https://live.example.com/1001",
                }),
            outputPath);

        var anchor = await anchored.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(anchor <= DateTimeOffset.UtcNow.AddSeconds(1));

        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotNull(recorder.AnchorUtc);

        await recorder.DisposeAsync();
        await recorder.DisposeAsync(); // 幂等
    }

    [Fact]
    public async Task StartAsync_MissingExecutable_Throws_AndDisposeIsSafe()
    {
        var recorder = new FfmpegFullRecorder(
            Path.Combine(_directory, "definitely-missing-ffmpeg.exe"));

        await Assert.ThrowsAnyAsync<Exception>(() => recorder.StartAsync(
            new StreamQuality(
                Id: "test",
                DisplayName: "test",
                Height: 720,
                Fps: 30,
                StreamUrl: "http://127.0.0.1:1/live.flv",
                RequiredHeaders: new Dictionary<string, string>()),
            Path.Combine(_directory, "out.mkv")));

        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task StopGracefullyAsync_WithoutStart_IsNoOp()
    {
        var recorder = new FfmpegFullRecorder(
            _ffmpegPath ?? Path.Combine(_directory, "ffmpeg.exe"));

        await recorder.StopGracefullyAsync(TimeSpan.FromSeconds(1));
        Assert.False(recorder.IsRunning);
        Assert.Null(recorder.AnchorUtc);

        await recorder.DisposeAsync();
    }

    [Fact]
    public void ProcessJobGuard_CreatesKillOnCloseJob_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var guard = new ProcessJobGuard();
        Assert.True(guard.IsAvailable);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        Assert.True(guard.TryAssign(process));
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        Assert.True(process.HasExited);
    }
}
