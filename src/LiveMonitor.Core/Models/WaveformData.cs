namespace LiveMonitor.Core.Models;

/// <summary>
/// 时间轴双波形数据：音频峰值曲线 + 弹幕密度曲线，两路均已归一化到 [0, 1]。
/// 数组按时间顺序等宽分桶，桶数由生成方决定（默认 2000）。
/// </summary>
/// <param name="Duration">源视频总时长。</param>
/// <param name="AudioPeaks">每个时间桶的 PCM 绝对峰值（满量程归一化）。</param>
/// <param name="DanmakuDensity">每个时间桶的弹幕条数密度（按最大桶归一化）。</param>
/// <param name="MaxDanmakuPerBucket">单桶最多弹幕条数（原始计数，供 Tooltip 显示）。</param>
public sealed record WaveformData(
    TimeSpan Duration,
    float[] AudioPeaks,
    float[] DanmakuDensity,
    int MaxDanmakuPerBucket);
