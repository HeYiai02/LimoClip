using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 「边录边剪」的增量音频峰值提取器：只解码 <paramref name="fromTime"/> 到 <paramref name="toTime"/>
/// 区间为 4kHz 单声道 s16le PCM 管道，按时间桶累计最大绝对采样值。
/// <para>
/// 与 <see cref="WaveformService"/> 的全片一次性提取不同，本类不读写磁盘缓存、不要求视频时长完整，
/// 可在录制仍在写盘时对分片尾部做小段追加提取；返回数组按区间长度固定分桶（桶数 =
/// <c>ceil((toTime-fromTime) * peaksPerSecond)</c>，EOF 后不足部分补 0），方便调用方直接拼接。
/// </para>
/// </summary>
public sealed class LiveWaveformDeltaExtractor : ILiveWaveformExtractor
{
    private const int SampleRate = 4000;
    private const int ReadBufferSize = 8192;
    private const int MinPeaksPerSecond = 1;
    private const int MaxPeaksPerSecond = 100;
    private const int TailLength = 400;

    private readonly string _ffmpegPath;

    /// <param name="ffmpegPath">ffmpeg 可执行文件路径。</param>
    public LiveWaveformDeltaExtractor(string ffmpegPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
    }

    /// <inheritdoc />
    public async Task<float[]> ExtractPeaksAsync(
        string videoPath,
        TimeSpan fromTime,
        TimeSpan toTime,
        int peaksPerSecond = 2,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("找不到要提取波形的视频文件。", videoPath);
        }

        peaksPerSecond = Math.Clamp(peaksPerSecond, MinPeaksPerSecond, MaxPeaksPerSecond);

        var from = fromTime < TimeSpan.Zero ? TimeSpan.Zero : fromTime;
        if (toTime <= from)
        {
            return Array.Empty<float>();
        }

        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg，无法提取音频波形。", _ffmpegPath);
        }

        var duration = toTime - from;
        // 桶数固定按请求区间计算：调用方用它按“每秒 peaksPerSecond 个桶”的固定分辨率拼接。
        var bucketCount = (int)Math.Ceiling(duration.TotalSeconds * peaksPerSecond);
        if (bucketCount <= 0)
        {
            return Array.Empty<float>();
        }

        var peaks = new float[bucketCount];
        var samplesPerBucket = SampleRate / (double)peaksPerSecond;

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
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(FormatSeconds(from));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(FormatSeconds(duration));
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
                // 分片尾部可能尚不可解码（录制正在写）或纯视频无音轨：
                // 上层会把它当作“本次没有可提取数据”处理，不因此中断同步。
                throw new InvalidOperationException(
                    $"提取音频峰值失败（ffmpeg exit={process.ExitCode}）：{Tail(stderr)}");
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
            var bucket = (int)(sampleIndex / samplesPerBucket);
            if (bucket >= peaks.Length)
            {
                // 文件实际比预期长（或时间基准有偏差）时，超出部分并入最后一个桶。
                bucket = peaks.Length - 1;
            }

            var peak = Math.Abs((int)sample) / 32768f;
            if (peak > peaks[bucket])
            {
                peaks[bucket] = peak;
            }

            sampleIndex++;
        }
    }

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Tail(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= TailLength ? text.Trim() : text[^TailLength..].Trim();
    }
}
