using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 播放器健康采样（mpv property → <see cref="PlayerHealthMetrics"/>）的纯规则，便于单测。
/// </summary>
internal static class PlayerHealthSampling
{
    /// <summary>
    /// 用 container-fps（源帧率）判断解码是否跟得上；estimated-vf-fps 会被 vf 滤镜影响，仅作兜底。
    /// </summary>
    public static double ResolveFramesPerSecond(double? containerFps, double? estimatedVfFps)
        => containerFps ?? estimatedVfFps ?? 0;

    /// <summary>
    /// mpv 的 hwdec-current 为空或 "no" 表示没有启用硬件解码。
    /// </summary>
    public static bool IsHardwareDecoding(string? hwdecCurrent)
        => !string.IsNullOrWhiteSpace(hwdecCurrent)
           && !string.Equals(hwdecCurrent, "no", StringComparison.OrdinalIgnoreCase);

    public static PlayerHealthMetrics Build(
        long? decoderDroppedFrames,
        long? droppedFrames,
        double? containerFps,
        double? estimatedVfFps,
        string? hwdecCurrent,
        DateTimeOffset sampledAt)
        => new(
            decoderDroppedFrames ?? 0,
            droppedFrames ?? 0,
            ResolveFramesPerSecond(containerFps, estimatedVfFps),
            IsHardwareDecoding(hwdecCurrent),
            hwdecCurrent,
            sampledAt);
}
