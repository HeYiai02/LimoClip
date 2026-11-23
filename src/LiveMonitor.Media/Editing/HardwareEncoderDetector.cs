using System.Diagnostics;
using System.Text;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 硬件 H.264 编码器探测器：按 nvenc → qsv → amf → libx264 顺序各做一次 0.1s 空输入实测，
/// 首个成功者胜出并缓存；探测失败只记录日志，不影响导出回退到 libx264。
/// </summary>
public sealed class HardwareEncoderDetector : IHardwareEncoderDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly (string Name, string[] ExtraArgs)[] Candidates =
    [
        ("h264_nvenc", ["-preset", "p4", "-cq", "20"]),
        ("h264_qsv", ["-preset", "medium", "-global_quality", "20"]),
        ("h264_amf", ["-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20"]),
        ("libx264", ["-preset", "veryfast", "-crf", "20"])
    ];

    private readonly string _ffmpegPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedEncoder;

    public HardwareEncoderDetector(string ffmpegPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>最近一次探测过程摘要，例如 <c>h264_nvenc=失败; ...; 选定 libx264</c>。</summary>
    public string? DetectionLog { get; private set; }

    /// <inheritdoc />
    public async Task<(string EncoderName, string[] ExtraArgs)> DetectBestAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedEncoder is not null)
            {
                return Candidates.First(candidate => candidate.Name == _cachedEncoder);
            }

            var log = new StringBuilder();
            foreach (var candidate in Candidates)
            {
                if (candidate.Name == "libx264")
                {
                    log.Append("选定 libx264");
                    DetectionLog = log.ToString();
                    _cachedEncoder = candidate.Name;
                    Debug.WriteLine($"[HardwareEncoderDetector] {DetectionLog}");
                    return candidate;
                }

                ct.ThrowIfCancellationRequested();
                var probed = await ProbeEncoderAsync(candidate.Name, ct).ConfigureAwait(false);
                log.Append(candidate.Name).Append(probed ? "=成功; " : "=失败; ");
                if (probed)
                {
                    log.Append("选定 ").Append(candidate.Name);
                    DetectionLog = log.ToString();
                    _cachedEncoder = candidate.Name;
                    Debug.WriteLine($"[HardwareEncoderDetector] {DetectionLog}");
                    return candidate;
                }
            }

            // 理论不可达：循环最后一项是 libx264。
            _cachedEncoder = "libx264";
            DetectionLog = log.ToString();
            return Candidates[^1];
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> ProbeEncoderAsync(string encoderName, CancellationToken ct)
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
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("color=c=black:s=256x256:d=0.1");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add(encoderName);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // 必须持续排空 stderr，否则 qsv/amf 的初始化告警可能塞满管道导致 WaitForExit 卡死。
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return false;
            }

            _ = await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
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
            // 进程可能刚好退出。
        }
    }
}
