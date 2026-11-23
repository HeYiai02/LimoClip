using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 双波形提取服务：用 ffmpeg 把视频解码为 4kHz 单声道 s16le PCM 流式管道，
/// 逐样本累计每个时间桶的绝对峰值；弹幕密度复用 <see cref="DanmakuJsonlReader"/> 聚合。
/// 内存只与桶数相关（2000 桶约 16KB），与录像时长无关；提取结果落本地 JSON 缓存供二次秒开。
/// </summary>
public sealed class WaveformService : IWaveformService
{
    private const int SampleRate = 4000;
    private const int MinBucketCount = 16;
    private const int MaxBucketCount = 20_000;
    private const int ReadBufferSize = 8192;
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = false };

    private readonly string _ffmpegPath;
    private readonly string _cacheDirectory;

    /// <param name="ffmpegPath">ffmpeg 可执行文件路径。</param>
    /// <param name="cacheDirectory">波形缓存目录；为 null 时用默认目录。</param>
    public WaveformService(string ffmpegPath, string? cacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? ResolveDefaultCacheDirectory()
            : Path.GetFullPath(cacheDirectory);
    }

    /// <summary>当前使用的波形缓存目录。</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>默认缓存目录：<c>%LocalAppData%/LiveMonitor/cache/waveforms</c>，不可写时回退程序目录。</summary>
    public static string DefaultCacheDirectory => ResolveDefaultCacheDirectory();

    /// <inheritdoc />
    public async Task<WaveformData> GetWaveformAsync(
        string videoPath,
        TimeSpan duration,
        string? companionDanmakuPath = null,
        int bucketCount = 2000,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("找不到要提取波形的视频文件。", videoPath);
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "视频时长必须大于 0。");
        }

        bucketCount = Math.Clamp(bucketCount, MinBucketCount, MaxBucketCount);

        var cachePath = BuildCachePath(videoPath, bucketCount);
        if (TryReadCache(cachePath, bucketCount, out var cached))
        {
            return cached;
        }

        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg，无法提取音频波形。", _ffmpegPath);
        }

        var peaks = await ExtractAudioPeaksAsync(videoPath, duration, bucketCount, ct).ConfigureAwait(false);
        var (density, maxPerBucket) = await BuildDanmakuDensityAsync(
            companionDanmakuPath, duration, bucketCount, ct).ConfigureAwait(false);

        var result = new WaveformData(duration, peaks, density, maxPerBucket);
        await TryWriteCacheAsync(cachePath, result).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 启动 ffmpeg 解码 PCM 并逐桶累计峰值。无音轨视频（ffmpeg 以非零码退出且 0 采样）降级为全 0 峰值，
    /// 部分可读时接受已解码样本，不因尾部损坏让整个时间轴失败。
    /// </summary>
    private async Task<float[]> ExtractAudioPeaksAsync(
        string videoPath,
        TimeSpan duration,
        int bucketCount,
        CancellationToken ct)
    {
        var peaks = new float[bucketCount];
        var totalSeconds = duration.TotalSeconds;
        var totalSamples = (long)Math.Ceiling(totalSeconds * SampleRate);
        var samplesPerBucket = totalSeconds * SampleRate / bucketCount;
        if (samplesPerBucket < 1)
        {
            samplesPerBucket = 1;
        }

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 ffmpeg 提取音频波形。");
        }

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
                    // 取消路径不抛：进程可能刚好退出。
                }
            },
            process);

        var stderrTask = process.StandardError.ReadToEndAsync();
        var buffer = new byte[ReadBufferSize];
        var leftover = -1;
        long sampleIndex = 0;

        try
        {
            var stdout = process.StandardOutput.BaseStream;
            while (true)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var index = 0;
                if (leftover >= 0)
                {
                    Accumulate((short)(leftover | (buffer[0] << 8)));
                    leftover = -1;
                    index = 1;
                }

                for (; index + 1 < read; index += 2)
                {
                    Accumulate(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2)));
                }

                if (index < read)
                {
                    leftover = buffer[index];
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 && sampleIndex == 0)
            {
                // 常见于纯视频无音轨：ffmpeg 报 "Output file does not contain any stream"，时间轴仍可用。
                Debug.WriteLine($"波形提取未得到音频采样（exit={process.ExitCode}）：{Tail(stderr)}");
                return peaks;
            }

            if (process.ExitCode != 0)
            {
                // 录像被强杀等场景可能拿到部分音频，保留已解码部分。
                Debug.WriteLine($"波形提取提前结束（exit={process.ExitCode}），已接受 {sampleIndex} 个采样。");
            }

            return peaks;
        }
        finally
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
                // 已被取消回调杀掉。
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 忽略收尾等待异常。
            }
        }

        void Accumulate(short sample)
        {
            var currentIndex = sampleIndex++;
            if (currentIndex >= totalSamples)
            {
                return; // 超出探测时长的尾部样本（容器时长取整误差）不参与分桶。
            }

            var bucket = (int)(currentIndex / samplesPerBucket);
            if (bucket >= bucketCount)
            {
                bucket = bucketCount - 1;
            }

            var amplitude = MathF.Abs(sample) / 32768f;
            if (amplitude > peaks[bucket])
            {
                peaks[bucket] = amplitude;
            }
        }
    }

    /// <summary>按 offsetMs 把弹幕聚合成密度桶并归一化；无 jsonl 或读失败时全 0。</summary>
    private static async Task<(float[] Density, int MaxPerBucket)> BuildDanmakuDensityAsync(
        string? jsonlPath,
        TimeSpan duration,
        int bucketCount,
        CancellationToken ct)
    {
        var density = new float[bucketCount];
        if (string.IsNullOrWhiteSpace(jsonlPath) || !File.Exists(jsonlPath))
        {
            return (density, 0);
        }

        IReadOnlyList<RecordedDanmakuItem> items;
        try
        {
            items = await DanmakuJsonlReader.ReadAllAsync(jsonlPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"读取伴随弹幕失败，密度曲线降级为全 0：{ex.Message}");
            return (density, 0);
        }

        if (items.Count == 0)
        {
            return (density, 0);
        }

        var msPerBucket = duration.TotalMilliseconds / bucketCount;
        if (msPerBucket <= 0)
        {
            return (density, 0);
        }

        var counts = new int[bucketCount];
        var max = 0;
        foreach (var item in items)
        {
            var index = (int)(item.OffsetMs / msPerBucket);
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= bucketCount)
            {
                index = bucketCount - 1;
            }

            var count = ++counts[index];
            if (count > max)
            {
                max = count;
            }
        }

        if (max > 0)
        {
            for (var i = 0; i < bucketCount; i++)
            {
                density[i] = counts[i] / (float)max;
            }
        }

        return (density, max);
    }

    private string BuildCachePath(string videoPath, int bucketCount)
    {
        var fullPath = Path.GetFullPath(videoPath);
        var info = new FileInfo(fullPath);
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{bucketCount}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, hash + ".wf.json");
    }

    private static bool TryReadCache(string cachePath, int bucketCount, out WaveformData data)
    {
        data = null!;
        try
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            var bytes = File.ReadAllBytes(cachePath);
            var cached = JsonSerializer.Deserialize<WaveformData>(bytes, CacheJsonOptions);
            if (cached is null
                || cached.AudioPeaks.Length != bucketCount
                || cached.DanmakuDensity.Length != bucketCount)
            {
                return false;
            }

            data = cached;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"波形缓存读取失败，重新提取：{ex.Message}");
            return false;
        }
    }

    private static async Task TryWriteCacheAsync(string cachePath, WaveformData data)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, CacheJsonOptions);
            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 缓存只是加速手段，写失败不影响本次结果。
            Debug.WriteLine($"波形缓存写入失败：{ex.Message}");
        }
    }

    private static string ResolveDefaultCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            try
            {
                var directory = Path.Combine(localAppData, "LiveMonitor", "cache", "waveforms");
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Debug.WriteLine($"默认波形缓存目录不可用，回退程序目录：{ex.Message}");
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "cache", "waveforms");
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
            // 完全不可写时缓存写入自会失败，这里无需再抛。
        }

        return fallback;
    }

    private static string Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }
}
