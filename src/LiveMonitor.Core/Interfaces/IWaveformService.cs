using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 时间轴双波形提取服务：流式解码音频峰值 + 聚合弹幕密度，并带本地缓存。
/// 实现必须保证长录像（4 小时级）提取时内存占用与录像时长无关。
/// </summary>
public interface IWaveformService
{
    /// <summary>
    /// 获取源视频的双波形数据；命中缓存时直接返回。
    /// </summary>
    /// <param name="videoPath">源视频路径。</param>
    /// <param name="duration">已知视频总时长，用于把 PCM 样本映射到时间桶。</param>
    /// <param name="companionDanmakuPath">伴随弹幕 .jsonl；为 null 或不存在时密度曲线全 0。</param>
    /// <param name="bucketCount">时间桶数量（默认 2000）。</param>
    /// <param name="ct">取消标记。</param>
    Task<WaveformData> GetWaveformAsync(
        string videoPath,
        TimeSpan duration,
        string? companionDanmakuPath = null,
        int bucketCount = 2000,
        CancellationToken ct = default);
}
