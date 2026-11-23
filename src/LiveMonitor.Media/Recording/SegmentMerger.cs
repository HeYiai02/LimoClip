using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveMonitor.Media.Recording;

/// <summary>一个录制分片及其在服务器时间轴上的位置信息。</summary>
/// <param name="Path">分片文件路径。</param>
/// <param name="Duration">分片内容时长（来自 ffmpeg 进度）；未知时为 <see cref="TimeSpan.Zero"/>。</param>
/// <param name="SourceTimelineStartSeconds">
/// 输入流自身时间轴起点（秒），即 ffmpeg 打开流时读到的 <c>start: X</c>；解析不到时为 null。
/// </param>
/// <param name="StartOffset">
/// 本分片开始录制时距本场录制开始的墙钟偏移，用于在服务器缓存重发时估算分片时间轴位置；
/// 未知时为 null（回退使用 <paramref name="SourceTimelineStartSeconds"/>）。
/// </param>
public sealed record RecordedSegment(
    string Path,
    TimeSpan Duration,
    double? SourceTimelineStartSeconds,
    TimeSpan? StartOffset = null);

/// <summary>分段录制产生的多个视频分片合并结果。</summary>
/// <param name="Merged">是否成功合并（失败时分片文件会保留，便于人工补救）。</param>
/// <param name="FileSizeBytes">合并后文件字节数；合并失败时返回各分片字节数之和。</param>
/// <param name="ErrorMessage">失败原因；成功时为 null。</param>
/// <param name="Duration">合并后内容时长（已扣除被裁掉的重叠部分）；未知时为 <see cref="TimeSpan.Zero"/>。</param>
public sealed record SegmentMergeResult(bool Merged, long FileSizeBytes, string? ErrorMessage, TimeSpan Duration);

/// <summary>
/// 把同一场直播录制的多个 mkv 分片按录制顺序合并成一个文件。
/// 虎牙等 CDN 会周期断开连接，单次 ffmpeg 无法连续录制，因此录制改为分段进行，
/// 停止时再由这里用 ffmpeg concat demuxer 无损拼接；拼接前按服务器时间轴裁掉重复内容。
/// </summary>
public interface ISegmentMerger
{
    Task<SegmentMergeResult> MergeAsync(
        string outputPath,
        IReadOnlyList<RecordedSegment> segments,
        CancellationToken ct = default);
}

/// <summary>
/// 计算每个分片需要裁掉的开头时长。虎牙等 CDN 每次重连都会从缓存的关键帧重新下发一小段，
/// 与上一段尾部重叠；用输入流绝对时间轴（<c>start: X</c>）对齐即可求出重叠量。
/// 时间轴不连续（例如流自身从 0 重新计时）时不做裁剪，避免把正常内容裁掉。
/// </summary>
public static class SegmentOverlapPlanner
{
    /// <summary>
    /// 每个分片至少保留的时长。虎牙 CDN 断流前常把缓存的关键帧整段重播一遍，
    /// 此时分片内容几乎全是重复的，允许裁到只剩 0.2 秒，避免每段都留下尾部的重复画面。
    /// </summary>
    public static readonly TimeSpan MinimumKeepDuration = TimeSpan.FromSeconds(0.2);

    /// <summary>输入流从 0 重新计时的判定阈值：起点小于该值视为新的时间轴。</summary>
    private const double TimelineRestartStartSeconds = 0.5;

    /// <summary>与前一段末尾相比，重叠超过该值才认为需要裁剪（过滤抖动）。</summary>
    private const double MinimumTrimSeconds = 0.05;

    public static IReadOnlyList<TimeSpan> ComputeStartTrims(IReadOnlyList<RecordedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var trims = new List<TimeSpan>(segments.Count);
        double? previousEnd = null;

        foreach (var segment in segments)
        {
            var durationSeconds = segment.Duration > TimeSpan.Zero ? segment.Duration.TotalSeconds : 0d;
            var trimSeconds = 0d;
            // 优先用墙钟起点：虎牙 CDN 每次重连都从缓存关键帧重发，ffmpeg 报告的时间轴起点
            // 与真实播放位置存在偏差；墙钟起点配合实测时长能更准确地还原分片的播放位置。
            var start = segment.StartOffset?.TotalSeconds ?? segment.SourceTimelineStartSeconds;

            if (start is { } startSeconds && previousEnd is { } endSeconds)
            {
                var restartedTimeline = startSeconds < TimelineRestartStartSeconds && endSeconds > 2.0;
                if (!restartedTimeline)
                {
                    var overlap = endSeconds - startSeconds;
                    if (overlap > MinimumTrimSeconds && durationSeconds > 0)
                    {
                        var maxTrim = Math.Max(0d, durationSeconds - MinimumKeepDuration.TotalSeconds);
                        trimSeconds = Math.Min(overlap, maxTrim);
                    }
                }
            }

            trims.Add(TimeSpan.FromSeconds(trimSeconds));

            if (start is { } timelineStart && durationSeconds > 0)
            {
                previousEnd = timelineStart + durationSeconds;
            }
            else
            {
                previousEnd = null;
            }
        }

        return trims;
    }

    /// <summary>合并后内容时长 = 各分片时长之和 − 被裁掉的重叠部分。</summary>
    public static TimeSpan ComputeContentDuration(IReadOnlyList<RecordedSegment> segments)
    {
        var trims = ComputeStartTrims(segments);
        var totalSeconds = 0d;
        for (var index = 0; index < segments.Count; index++)
        {
            var duration = segments[index].Duration > TimeSpan.Zero ? segments[index].Duration.TotalSeconds : 0d;
            totalSeconds += Math.Max(0d, duration - trims[index].TotalSeconds);
        }

        return TimeSpan.FromSeconds(totalSeconds);
    }
}

/// <summary>基于 ffmpeg concat demuxer 的默认实现：<c>-c copy</c> 无损拼接，不做重编码。</summary>
public sealed class FfmpegSegmentMerger : ISegmentMerger
{
    private readonly string _ffmpegPath;
    private readonly Action<string>? _log;

    public FfmpegSegmentMerger(string ffmpegPath, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public async Task<SegmentMergeResult> MergeAsync(
        string outputPath,
        IReadOnlyList<RecordedSegment> segments,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(segments);

        var existing = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Path) && File.Exists(segment.Path))
            .ToList();
        existing = await MeasureDurationsAsync(existing, ct).ConfigureAwait(false);
        var totalSize = existing.Sum(segment => TryGetFileLength(segment.Path));
        var contentDuration = SegmentOverlapPlanner.ComputeContentDuration(existing);
        if (existing.Count == 0)
        {
            return new SegmentMergeResult(false, 0, "没有可用的录制分片。", TimeSpan.Zero);
        }

        if (existing.Count == 1)
        {
            return MergeSingle(existing[0], outputPath);
        }

        var trims = SegmentOverlapPlanner.ComputeStartTrims(existing);
        var listPath = outputPath + ".concat.txt";
        try
        {
            var lines = new List<string>(existing.Count * 2);
            for (var index = 0; index < existing.Count; index++)
            {
                lines.Add(BuildConcatLine(existing[index].Path));
                var trim = trims[index];
                if (trim.TotalSeconds > 0.05)
                {
                    lines.Add("inpoint " + trim.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }

            await File.WriteAllLinesAsync(listPath, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);

            var arguments = new List<string>
            {
                "-hide_banner",
                "-nostats",
                "-loglevel",
                "warning",
                "-f",
                "concat",
                "-safe",
                "0",
                "-i",
                listPath,
                "-c",
                "copy",
                "-f",
                "matroska",
                "-y",
                outputPath,
            };

            var (exitCode, stderrTail, timedOut) = await RunFfmpegAsync(arguments, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (exitCode == 0 && File.Exists(outputPath) && TryGetFileLength(outputPath) > 0)
            {
                foreach (var segment in existing)
                {
                    TryDelete(segment.Path);
                }

                var mergedSize = TryGetFileLength(outputPath);
                _log?.Invoke(
                    $"录制分片合并完成：{existing.Count} 段 → {outputPath}（{mergedSize / 1024.0 / 1024.0:0.0} MB，" +
                    $"裁剪重叠 {trims.Sum(trim => trim.TotalSeconds):0.0} 秒）。");
                return new SegmentMergeResult(true, mergedSize, null, contentDuration);
            }

            var reason = timedOut
                ? "ffmpeg 合并分片超时。"
                : $"ffmpeg 合并分片失败（exit={exitCode}）{(string.IsNullOrWhiteSpace(stderrTail) ? string.Empty : "：" + stderrTail)}";
            _log?.Invoke($"录制分片合并失败（{existing.Count} 段，分片已保留）：{reason}");
            return new SegmentMergeResult(false, totalSize, reason, TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"录制分片合并异常（分片已保留）：{ex.Message}");
            return new SegmentMergeResult(false, totalSize, ex.Message, TimeSpan.Zero);
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    /// <summary>
    /// 用 ffmpeg 快速统计每个分片的实际时长（只解封装、不转码）。录制期间进度回调可能滞后，
    /// 直接用文件时长计算重叠更准；测量失败则保留原时长，交给 planner 整段保留。
    /// </summary>
    private async Task<List<RecordedSegment>> MeasureDurationsAsync(
        List<RecordedSegment> segments,
        CancellationToken ct)
    {
        if (segments.Count <= 1)
        {
            return segments;
        }

        var measured = new List<RecordedSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var duration = await TryMeasureDurationAsync(segment.Path, ct).ConfigureAwait(false);
            measured.Add(duration is { } value && value > TimeSpan.Zero
                ? segment with { Duration = value }
                : segment);
        }

        return measured;
    }

    private async Task<TimeSpan?> TryMeasureDurationAsync(string path, CancellationToken ct)
    {
        var nullTarget = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-nostats");
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-progress");
            startInfo.ArgumentList.Add("pipe:1");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add(nullTarget);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            long lastOutTimeMicroseconds = 0;
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    throw;
                }

                if (line is null)
                {
                    break;
                }

                const string prefix = "out_time_us=";
                if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                    long.TryParse(
                        line.AsSpan(prefix.Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value) &&
                    value > 0)
                {
                    lastOutTimeMicroseconds = value;
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _ = await stderrTask.ConfigureAwait(false);
                return null;
            }

            _ = await stderrTask.ConfigureAwait(false);
            return lastOutTimeMicroseconds > 0
                ? TimeSpan.FromTicks(lastOutTimeMicroseconds * 10)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"统计录制分片时长失败（{Path.GetFileName(path)}）：{ex.Message}");
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static SegmentMergeResult MergeSingle(RecordedSegment segment, string outputPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(segment.Path, outputPath, overwrite: true);
            return new SegmentMergeResult(true, TryGetFileLength(outputPath), null, segment.Duration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SegmentMergeResult(false, TryGetFileLength(segment.Path), ex.Message, TimeSpan.Zero);
        }
    }

    /// <summary>ffmpeg concat 文件要求每行 <c>file '路径'</c>，单引号需转义为 <c>'\''</c>。</summary>
    private static string BuildConcatLine(string path)
    {
        var fullPath = Path.GetFullPath(path).Replace('\\', '/');
        var escaped = fullPath.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"file '{escaped}'";
    }

    private async Task<(int ExitCode, string? StderrTail, bool TimedOut)> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return (-1, "ffmpeg 进程启动失败。", false);
        }

        // 必须持续读取 stdout/stderr，否则管道写满会让 ffmpeg 挂起。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(60 + arguments.Count * 2, 60, 600));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        var stderr = await ReadTailAsync(stderrTask).ConfigureAwait(false);
        await ReadTailAsync(stdoutTask).ConfigureAwait(false);
        return (timedOut ? -1 : process.ExitCode, stderr, timedOut);
    }

    private static async Task<string?> ReadTailAsync(Task<string> task)
    {
        try
        {
            var text = await task.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            return trimmed.Length <= 1200 ? trimmed : trimmed[^1200..];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long TryGetFileLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
