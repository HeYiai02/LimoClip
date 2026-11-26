using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 双版导出器：无弹幕版走 <c>-c copy</c> 无损截取，有弹幕版把 .jsonl 转 ASS 后由 ffmpeg 烧录进画面。
/// 导出全程挂 <see cref="ProcessJobGuard"/>，取消/失败时删除残缺输出与临时字幕目录。
/// </summary>
public sealed class ClipExporter : IClipExporter
{
    private const double ProgressCap = 99.5;

    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;
    private readonly HardwareEncoderDetector _encoderDetector;
    private readonly DanmakuAssConverter _assConverter;

    public ClipExporter(
        string ffmpegPath,
        ProcessJobGuard? jobGuard,
        HardwareEncoderDetector encoderDetector,
        DanmakuAssConverter assConverter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
        _encoderDetector = encoderDetector;
        _assConverter = assConverter;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExportAsync(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
        string sourceVideoCodec,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EndTime <= request.StartTime)
        {
            throw new ArgumentException("出点时间必须大于入点时间。", nameof(request));
        }

        if (!File.Exists(request.InputVideoPath))
        {
            throw new FileNotFoundException("找不到要导出的源视频文件。", request.InputVideoPath);
        }

        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg，无法导出。", _ffmpegPath);
        }

        ArgumentException.ThrowIfNullOrEmpty(request.OutputBaseName);
        Directory.CreateDirectory(request.OutputDirectory);
        ct.ThrowIfCancellationRequested();

        var outputs = new List<string>();
        var clipDuration = request.EndTime - request.StartTime;
        var needClean = request.Mode is ClipExportMode.CleanOnly or ClipExportMode.Both;
        var needBurned = request.Mode is ClipExportMode.BurnedDanmaku or ClipExportMode.Both;

        if (needClean)
        {
            var suffix = request.Mode == ClipExportMode.Both ? "_clean" : string.Empty;
            var outputPath = Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{suffix}.mp4");
            await ExportCleanLosslessAsync(
                    request.InputVideoPath,
                    outputPath,
                    request.StartTime,
                    clipDuration,
                    sourceVideoCodec,
                    progress,
                    ct)
                .ConfigureAwait(false);
            outputs.Add(outputPath);
        }

        if (needBurned)
        {
            var suffix = request.Mode == ClipExportMode.Both ? "_danmaku" : string.Empty;
            var outputPath = Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{suffix}.mp4");
            await ExportWithBurnedDanmakuAsync(
                    request,
                    danmakuItems ?? [],
                    outputPath,
                    clipDuration,
                    progress,
                    ct)
                .ConfigureAwait(false);
            outputs.Add(outputPath);
        }

        return outputs;
    }

    private async Task ExportCleanLosslessAsync(
        string inputPath,
        string outputPath,
        TimeSpan start,
        TimeSpan duration,
        string sourceCodec,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-ss", FormatSeconds(start),
            "-i", inputPath,
            "-t", FormatSeconds(duration),
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
        };

        if (string.Equals(sourceCodec, "hevc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCodec, "h265", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-tag:v");
            arguments.Add("hvc1");
        }

        arguments.AddRange(["-progress", "pipe:1", "-y", outputPath]);

        await RunExportAsync(
                arguments,
                workingDirectory: null,
                outputPath,
                duration,
                "正在无损导出无弹幕版...",
                progress,
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExportWithBurnedDanmakuAsync(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem> danmakuItems,
        string outputPath,
        TimeSpan duration,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"LimoClip_Export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var assPath = Path.Combine(tempDirectory, "danmaku.ass");

        try
        {
            progress?.Report(new ClipExportProgress("正在生成 ASS 弹幕字幕...", 2, null));
            await _assConverter.ConvertToAssAsync(
                    danmakuItems,
                    request.StartTime,
                    request.EndTime,
                    assPath,
                    request.AssStyle,
                    ct)
                .ConfigureAwait(false);

            var (encoderName, extraArgs) = await _encoderDetector.DetectBestAsync(ct).ConfigureAwait(false);

            var arguments = new List<string>
            {
                "-hide_banner", "-nostats", "-loglevel", "error",
                "-ss", FormatSeconds(request.StartTime),
                "-i", request.InputVideoPath,
                "-t", FormatSeconds(duration),
                // WorkingDirectory 已切到临时目录，这里写死相对文件名，彻底避开 Windows 路径转义问题。
                "-vf", "ass=danmaku.ass",
                "-c:v", encoderName,
            };
            arguments.AddRange(extraArgs);
            arguments.AddRange(
            [
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                "-progress", "pipe:1",
                "-y", outputPath,
            ]);

            await RunExportAsync(
                    arguments,
                    tempDirectory,
                    outputPath,
                    duration,
                    $"正在烧录弹幕导出 ({encoderName})...",
                    progress,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"清理临时字幕目录失败：{ex.Message}");
            }
        }
    }

    private async Task RunExportAsync(
        List<string> arguments,
        string? workingDirectory,
        string outputPath,
        TimeSpan targetDuration,
        string stageLabel,
        IProgress<ClipExportProgress>? progress,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
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
            throw new InvalidOperationException("无法启动 ffmpeg 导出进程。");
        }

        _jobGuard?.TryAssign(process);

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var mergedProgress = progress is null ? null : new MergedProgress(progress);

        try
        {
            using var registration = ct.Register(
                static state =>
                {
                    try
                    {
                        var target = (Process)state!;
                        if (!target.HasExited)
                        {
                            target.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // 进程可能刚好退出。
                    }
                },
                process);

            var totalUs = Math.Max(1.0, targetDuration.TotalMicroseconds);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ReportProgressLine(line, totalUs, stageLabel, mergedProgress);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"FFmpeg 导出失败 (ExitCode={process.ExitCode}): {Tail(error)}");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException("FFmpeg 导出结束但输出文件不存在或为空。");
            }

            progress?.Report(new ClipExportProgress(stageLabel, 100, mergedProgress?.LastSpeed));
        }
        catch
        {
            // 取消或报错时清理残缺文件。
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"删除残缺导出文件失败：{ex.Message}");
            }

            throw;
        }
    }

    private static void ReportProgressLine(
        string line,
        double totalMicroseconds,
        string stageLabel,
        MergedProgress? progress)
    {
        if (!FfmpegProgressParser.TryParseLine(line, out var update))
        {
            if (progress is not null
                && line.StartsWith("speed=", StringComparison.Ordinal)
                && TryParseSpeed(line["speed=".Length..], out var speed))
            {
                progress.LastSpeed = speed;
            }

            return;
        }

        if (progress is null || update.OutTimeMicroseconds is not { } microseconds)
        {
            return;
        }

        var percentage = Math.Clamp(microseconds / totalMicroseconds * 100.0, 0, ProgressCap);
        progress.Report(stageLabel, percentage);
    }

    private static bool TryParseSpeed(string raw, out double speed)
    {
        var trimmed = raw.Trim().TrimEnd('x', 'X');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
            && speed > 0;
    }

    private static string FormatSeconds(TimeSpan timeSpan)
        => timeSpan.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

    private static string Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 800 ? trimmed : trimmed[^800..];
    }

    /// <summary>把 ffmpeg 进度换算成百分比，并把最新 speed 合并进报告。</summary>
    private sealed class MergedProgress(IProgress<ClipExportProgress> inner)
    {
        public double? LastSpeed { get; set; }

        public void Report(string stageLabel, double percentage)
            => inner.Report(new ClipExportProgress(stageLabel, percentage, LastSpeed));
    }
}
