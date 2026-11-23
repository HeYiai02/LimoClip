using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 视频元信息探测：仓库只随包 ffmpeg（没有 ffprobe），因此统一调用
/// <c>ffmpeg -hide_banner -i &lt;file&gt;</c> 并解析 stderr 中的 Duration / Stream 行。
/// ffmpeg 无输出参数时退出码固定为 1，属正常现象。
/// </summary>
public sealed class VideoProbeService
{
    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(?<hours>\d+):(?<minutes>\d{1,2}):(?<seconds>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VideoStreamRegex = new(
        @"Stream\s+#\d+:\d+(?:\[[^\]]*\])?(?:\([^)]*\))?:\s*Video:\s*(?<codec>[^,\s]+)(?<rest>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResolutionRegex = new(
        @"(?<width>\d{2,5})x(?<height>\d{2,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FpsRegex = new(
        @"(?<fps>\d+(?:\.\d+)?)\s*fps",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _ffmpegPath;
    private readonly TimeSpan _probeTimeout;

    public VideoProbeService(string ffmpegPath, TimeSpan? probeTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 探测本地视频文件。文件不存在、ffmpeg 不存在、超时或输出无法解析时抛异常。
    /// </summary>
    public async Task<VideoMetadata> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("找不到要探测的视频文件。", videoPath);
        }

        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg，无法探测视频信息。", _ffmpegPath);
        }

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 ffmpeg 进程进行视频探测。");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_probeTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"探测视频信息超时（>{_probeTimeout.TotalSeconds:F0}s）：{videoPath}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (!TryParseProbeOutput(stderr, out var metadata))
        {
            throw new InvalidOperationException(
                $"无法从 ffmpeg 输出解析视频信息：{Path.GetFileName(videoPath)}{Environment.NewLine}{Truncate(stderr)}");
        }

        return metadata;
    }

    /// <summary>解析 <c>ffmpeg -i</c> 的 stderr；用于单元测试与真实探测共用同一套规则。</summary>
    public static bool TryParseProbeOutput(string? stderr, out VideoMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        var durationMatch = DurationRegex.Match(stderr);
        if (!durationMatch.Success)
        {
            return false;
        }

        var duration = new TimeSpan(
            hours: int.Parse(durationMatch.Groups["hours"].Value, CultureInfo.InvariantCulture),
            minutes: int.Parse(durationMatch.Groups["minutes"].Value, CultureInfo.InvariantCulture),
            seconds: 0)
            + TimeSpan.FromSeconds(double.Parse(durationMatch.Groups["seconds"].Value, CultureInfo.InvariantCulture));

        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var videoMatch = VideoStreamRegex.Match(stderr);
        if (!videoMatch.Success)
        {
            return false;
        }

        var rest = videoMatch.Groups["rest"].Value;
        var resolutionMatch = ResolutionRegex.Match(rest);
        if (!resolutionMatch.Success)
        {
            return false;
        }

        var width = int.Parse(resolutionMatch.Groups["width"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(resolutionMatch.Groups["height"].Value, CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var fpsMatch = FpsRegex.Match(rest);
        var fps = fpsMatch.Success
            ? double.Parse(fpsMatch.Groups["fps"].Value, CultureInfo.InvariantCulture)
            : 0;

        metadata = new VideoMetadata(
            duration,
            width,
            height,
            fps,
            NormalizeCodec(videoMatch.Groups["codec"].Value));
        return true;
    }

    private static string NormalizeCodec(string raw)
    {
        var codec = raw.Trim().ToLowerInvariant();
        return codec switch
        {
            "h265" => "hevc",
            _ => codec
        };
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
            // 进程可能已自行退出。
        }
    }

    private static string Truncate(string text)
        => text.Length <= 1600 ? text : text[..1600] + "…";
}
