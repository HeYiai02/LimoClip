namespace LiveMonitor.Core.Models;

/// <summary>
/// 播放器一次健康采样的快照。字段来自 mpv 的 property observation。
/// </summary>
public sealed record PlayerHealthMetrics(
    long DecoderDroppedFrames,
    long DroppedFrames,
    double FramesPerSecond,
    bool HardwareDecoding,
    string? HwdecCurrent,
    DateTimeOffset SampledAt)
{
    public static PlayerHealthMetrics Empty { get; } = new(
        0,
        0,
        0,
        false,
        null,
        DateTimeOffset.MinValue);
}
