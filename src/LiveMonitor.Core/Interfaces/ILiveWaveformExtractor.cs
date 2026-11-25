namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 按时间区间增量提取音频峰值，用于「边录边剪」时让波形随录制进度延展，
/// 避免对仍在增长的录像反复做全量波形提取。
/// </summary>
public interface ILiveWaveformExtractor
{
    /// <summary>
    /// 提取 <paramref name="fromTime"/> 到 <paramref name="toTime"/> 区间的音频峰值，
    /// 按 <paramref name="peaksPerSecond"/> 分桶返回（每桶取桶内最大绝对采样值，归一化到 [0,1]）。
    /// </summary>
    /// <param name="videoPath">视频文件路径（可以是录制中的分片）。</param>
    /// <param name="fromTime">区间起点（相对文件开头）；小于 0 按 0 处理。</param>
    /// <param name="toTime">区间终点（不含）；不大于起点时返回空数组。</param>
    /// <param name="peaksPerSecond">每秒峰值桶数（默认 2）。</param>
    /// <param name="ct">取消标记。</param>
    Task<float[]> ExtractPeaksAsync(
        string videoPath,
        TimeSpan fromTime,
        TimeSpan toTime,
        int peaksPerSecond = 2,
        CancellationToken ct = default);
}
