using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 全程录制器：调用 ffmpeg 以 <c>-c copy</c> 把直播流零重编码写入 <c>.mkv</c>。
/// <para>
/// 关键行为（见 limoclip_v0_3.md §2）：停录时向 stdin 写 <c>q</c> 等待优雅退出（超时才 Kill），
/// 保证 matroska 收尾写入索引；通过 <see cref="ProcessJobGuard"/> 防止宿主崩溃后残留 ffmpeg；
/// 以 <c>-progress pipe:1</c> 的第一个非零 <c>out_time_us</c> 反推首帧锚点，
/// 供弹幕 jsonl 计算相对录屏起点的毫秒偏移。
/// </para>
/// </summary>
public sealed class FfmpegFullRecorder : IFullStreamRecorder
{
    private const int StderrTailLimit = 4000;
    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly string? _caBundlePath;
    private readonly object _gate = new();
    private readonly StringBuilder _stderrTail = new();
    private readonly TaskCompletionSource<DateTimeOffset> _anchorCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _exitTask;
    private DateTimeOffset _processStartUtc;
    private long _anchorUtcTicks;
    private long _latestOutTimeMicroseconds;
    private long _latestTotalSizeBytes;
    private double _latestBitrateKbps;
    private bool _started;
    private int _exitReported;
    private int _disposed;

    public FfmpegFullRecorder(
        string ffmpegPath,
        ProcessJobGuard? jobGuard = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        string? caBundlePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);

        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _caBundlePath = string.IsNullOrWhiteSpace(caBundlePath) ? null : caBundlePath.Trim();
    }

    public event Action<DateTimeOffset>? OnFirstFrameAnchored;

    public event Action<TimeSpan, long, double>? OnProgressUpdated;

    public event Action<int, string?>? OnExited;

    /// <summary>首帧锚点（UTC）；尚未锚定或进程未启动时为 null。</summary>
    public DateTimeOffset? AnchorUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _anchorUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>等待首帧锚点（供上层 JSONL 写入器对齐时间轴）；进程失败时也会以启动时间锚定。</summary>
    public Task<DateTimeOffset> WaitForAnchorAsync(CancellationToken ct = default)
        => _anchorCompletion.Task.WaitAsync(ct);

    /// <summary>ffmpeg 进程是否仍在运行。</summary>
    public bool IsRunning
    {
        get
        {
            var process = _process;
            try
            {
                return process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async Task StartAsync(
        StreamQuality sourceQuality,
        string outputMkvPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceQuality);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMkvPath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_started)
            {
                throw new InvalidOperationException("录制器已经启动，不能重复启动。");
            }

            _started = true;
        }

        var arguments = FfmpegArgumentBuilder.Build(sourceQuality, outputMkvPath, _caBundlePath);
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputMkvPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _processStartUtc = _clock();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg 进程启动失败。");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        AssignJobIfAvailable(process);

        _stdoutTask = Task.Run(() => ReadStandardOutputAsync(process, token), CancellationToken.None);
        _stderrTask = Task.Run(() => ReadStandardErrorAsync(process, token), CancellationToken.None);
        _exitTask = Task.Run(() => WaitForExitAsync(process), CancellationToken.None);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await SendQuitAsync(process).ConfigureAwait(false);
                await WaitForExitWithTimeoutAsync(process, timeout).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出，忽略。
        }
        finally
        {
            EnsureAnchorFallback();
        }

        if (_exitTask is not null)
        {
            try
            {
                await _exitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await StopGracefullyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"录制器停止异常：{ex.Message}");
            }

            if (_exitTask is not null)
            {
                try
                {
                    await _exitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"等待 ffmpeg 退出异常：{ex.Message}");
                }
            }

            _cts?.Cancel();

            foreach (var task in new[] { _stdoutTask, _stderrTask })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"等待 ffmpeg 输出管道退出异常：{ex.Message}");
                }
            }

            process.Dispose();
            _process = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>若首帧锚点仍缺失（例如空流/立即失败），回退到进程启动时间并触发锚定事件。</summary>
    public void EnsureAnchorFallback()
    {
        if (AnchorUtc is not null)
        {
            return;
        }

        SetAnchor(_processStartUtc == default ? _clock() : _processStartUtc);
    }

    private async Task SendQuitAsync(Process process)
    {
        try
        {
            await process.StandardInput.WriteAsync("q\n").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log?.Invoke($"向 ffmpeg 发送 q 失败：{ex.Message}");
        }
    }

    private async Task WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (timeout == TimeSpan.Zero)
            {
                if (!process.HasExited)
                {
                    KillProcess(process);
                }

                return;
            }

            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"ffmpeg 在 {timeout.TotalSeconds:0.#} 秒内未响应 q，强制终止。");
            KillProcess(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _log?.Invoke($"终止 ffmpeg 失败：{ex.Message}");
        }
    }

    private async Task ReadStandardOutputAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!FfmpegProgressParser.TryParseLine(line, out var update))
                {
                    continue;
                }

                ApplyProgress(update);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke($"读取 ffmpeg 进度输出异常：{ex.Message}");
        }
    }

    private async Task ReadStandardErrorAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                AppendStderr(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke($"读取 ffmpeg 错误输出异常：{ex.Message}");
        }
    }

    private async Task WaitForExitAsync(Process process)
    {
        var exitCode = -1;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"等待 ffmpeg 退出异常：{ex.Message}");
        }

        EnsureAnchorFallback();

        try
        {
            if (_stdoutTask is not null)
            {
                await _stdoutTask.ConfigureAwait(false);
            }

            if (_stderrTask is not null)
            {
                await _stderrTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ffmpeg 输出读取收尾异常：{ex.Message}");
        }

        if (Interlocked.Exchange(ref _exitReported, 1) != 0)
        {
            return;
        }

        var summary = GetStderrTail();
        try
        {
            OnExited?.Invoke(exitCode, string.IsNullOrWhiteSpace(summary) ? null : summary);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"OnExited 回调异常：{ex.Message}");
        }
    }

    private void ApplyProgress(FfmpegProgressUpdate update)
    {
        if (update.TotalSizeBytes is { } totalSize)
        {
            Interlocked.Exchange(ref _latestTotalSizeBytes, totalSize);
        }

        if (update.OutTimeMicroseconds is { } outTimeMicroseconds)
        {
            Interlocked.Exchange(ref _latestOutTimeMicroseconds, outTimeMicroseconds);
        }

        if (update.BitrateKbps is { } bitrateKbps)
        {
            lock (_gate)
            {
                _latestBitrateKbps = bitrateKbps;
            }
        }

        if (update.OutTimeMicroseconds is > 0 && AnchorUtc is null)
        {
            // 首个非零 out_time_us：ffmpeg 已经拿到了第一帧数据，反推该帧在墙钟上的时间。
            var anchor = _clock() - TimeSpan.FromTicks(update.OutTimeMicroseconds!.Value * 10);
            SetAnchor(anchor);
        }

        var hasProgress = update.TotalSizeBytes is not null
            || update.OutTimeMicroseconds is not null
            || update.BitrateKbps is not null;
        if (!hasProgress)
        {
            return;
        }

        if (AnchorUtc is null)
        {
            return;
        }

        var duration = TimeSpan.FromTicks(
            Interlocked.Read(ref _latestOutTimeMicroseconds) * 10);
        double bitrate;
        lock (_gate)
        {
            bitrate = _latestBitrateKbps;
        }

        try
        {
            OnProgressUpdated?.Invoke(
                duration,
                Interlocked.Read(ref _latestTotalSizeBytes),
                bitrate);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"OnProgressUpdated 回调异常：{ex.Message}");
        }
    }

    private void SetAnchor(DateTimeOffset anchor)
    {
        if (Interlocked.CompareExchange(ref _anchorUtcTicks, anchor.UtcTicks, 0) != 0)
        {
            return;
        }

        _anchorCompletion.TrySetResult(anchor);

        try
        {
            OnFirstFrameAnchored?.Invoke(anchor);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"OnFirstFrameAnchored 回调异常：{ex.Message}");
        }
    }

    private void AssignJobIfAvailable(Process process)
    {
        if (_jobGuard is null)
        {
            return;
        }

        if (_jobGuard.TryAssign(process))
        {
            return;
        }

        if (_jobGuard.IsAvailable)
        {
            _log?.Invoke(
                $"把 ffmpeg 挂到 Job Object 失败（Win32Error={_jobGuard.LastAssignError}），" +
                "宿主异常退出时可能残留 ffmpeg。");
        }
    }

    private void AppendStderr(string line)
    {
        lock (_gate)
        {
            _stderrTail.AppendLine(line);
            if (_stderrTail.Length > StderrTailLimit)
            {
                _stderrTail.Remove(0, _stderrTail.Length - StderrTailLimit);
            }
        }
    }

    private string GetStderrTail()
    {
        lock (_gate)
        {
            return _stderrTail.ToString().Trim();
        }
    }

    /// <summary>供测试/诊断用：当前累计进度文本。</summary>
    public string DescribeProgress()
    {
        double bitrate;
        lock (_gate)
        {
            bitrate = _latestBitrateKbps;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"duration={TimeSpan.FromTicks(Interlocked.Read(ref _latestOutTimeMicroseconds) * 10):hh\\:mm\\:ss\\.fff} " +
            $"size={Interlocked.Read(ref _latestTotalSizeBytes)} bitrate={bitrate:0.#}kbps");
    }
}
