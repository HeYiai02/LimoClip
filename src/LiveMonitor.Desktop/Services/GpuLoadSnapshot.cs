namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 一次 GPU 负载采样（利用率 / 解码器占用）。NVIDIA 驱动不可用时两个字段都为 null，
/// 自适应画质策略会保持现状，不会因为「没有 GPU 指标」而误降级。
/// </summary>
public readonly record struct GpuLoadSnapshot(double? Utilization, double? Decoder)
{
    /// <summary>是否至少有一项有效采样。</summary>
    public bool HasSample => Utilization.HasValue || Decoder.HasValue;

    /// <summary>无采样的默认值。</summary>
    public static GpuLoadSnapshot Empty => default;
}
