using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 边录边剪增量波形的纯计算规则：增量提取窗口对齐、峰值数组拼接、0.5s 分桶重建
/// （音频峰值 + 弹幕密度归一化）。不依赖 Avalonia / 播放器 / 波形提取服务，便于单测。
/// </summary>
internal static class LiveWaveformLogic
{
    /// <summary>边录边剪波形分桶宽度（秒）；峰值与弹幕密度共用同一网格。</summary>
    internal const double PeakBucketSeconds = 0.5;

    /// <summary>边录边剪音轨峰值采样率（每秒桶数）。</summary>
    internal const int PeaksPerSecond = 2;

    /// <summary>一次增量提取的窗口：终点（对齐到桶边界）与应产出的峰值数量。</summary>
    internal readonly record struct ExtractionWindow(TimeSpan To, int ExpectedCount);

    /// <summary>
    /// 计算下一次增量提取窗口：终点向下对齐到 0.5s 桶网，避免亚桶相位漂移。
    /// 终点不晚于已提取位置时返回 false（无需提取）。
    /// </summary>
    internal static bool TryComputeExtractionWindow(
        TimeSpan safeClipDuration,
        TimeSpan extractedUntil,
        out ExtractionWindow window)
    {
        var toBucket = (int)Math.Floor(safeClipDuration.TotalSeconds * PeaksPerSecond);
        var to = TimeSpan.FromSeconds(toBucket / (double)PeaksPerSecond);
        var expectedCount = (int)Math.Round((to - extractedUntil).TotalSeconds * PeaksPerSecond);
        window = new ExtractionWindow(to, expectedCount);
        return to > extractedUntil;
    }

    /// <summary>
    /// 把新提取的峰值拼接到既有数组：严格按 <paramref name="expectedCount"/> 对齐
    /// （多出裁剪、不足补 0），保证时间轴与音频相位一致。数量非正时原样返回既有数组。
    /// </summary>
    internal static float[] AppendPeaks(float[]? existing, IReadOnlyList<float> peaks, int expectedCount)
    {
        var current = existing ?? Array.Empty<float>();
        if (expectedCount <= 0)
        {
            return current;
        }

        var merged = new float[current.Length + expectedCount];
        Array.Copy(current, merged, current.Length);
        var copyCount = Math.Min(peaks.Count, expectedCount);
        for (var i = 0; i < copyCount; i++)
        {
            merged[current.Length + i] = peaks[i];
        }

        return merged;
    }

    /// <summary>
    /// 按已录时长重建固定 0.5s 分辨率的波形数据：音频峰值按桶拷贝/补 0，
    /// 弹幕密度按毫秒偏移计数后按最大桶归一化。
    /// </summary>
    internal static WaveformData Build(
        TimeSpan recordedDuration,
        float[]? peaks,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems)
    {
        var bucketCount = Math.Max(1, (int)Math.Ceiling(recordedDuration.TotalSeconds / PeakBucketSeconds));
        var audioPeaks = new float[bucketCount];
        if (peaks is { Length: > 0 } source)
        {
            Array.Copy(source, audioPeaks, Math.Min(source.Length, bucketCount));
        }

        var density = new float[bucketCount];
        var maxPerBucket = 0;
        if (danmakuItems is { Count: > 0 } items)
        {
            var counts = new int[bucketCount];
            foreach (var item in items)
            {
                var index = (int)(item.OffsetMs / (PeakBucketSeconds * 1000));
                if (index < 0 || index >= bucketCount)
                {
                    continue;
                }

                counts[index]++;
                if (counts[index] > maxPerBucket)
                {
                    maxPerBucket = counts[index];
                }
            }

            if (maxPerBucket > 0)
            {
                for (var i = 0; i < bucketCount; i++)
                {
                    density[i] = counts[i] / (float)maxPerBucket;
                }
            }
        }

        return new WaveformData(recordedDuration, audioPeaks, density, maxPerBucket);
    }
}
