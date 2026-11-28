namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 多路直播的自适应画质策略：默认保持源帧率与画质，只有 GPU 持续高负载时才先压帧率（15fps 紧凑模式）、
/// 仍不够再压画质；负载恢复后自动解除压缩。纯函数，采样节奏与播放器副作用留在 <see cref="WorkspacePlayerManager"/>。
/// </summary>
public static class AdaptiveQualityPolicy
{
    /// <summary>GPU 利用率达到该值视为高负载（%）。</summary>
    public const double PressureUtilizationThreshold = 85;

    /// <summary>GPU 解码器占用达到该值视为高负载（%）。</summary>
    public const double PressureDecoderThreshold = 80;

    /// <summary>GPU 指标回落到该利用率以下才可能解除压缩（%）。</summary>
    public const double HealthyUtilizationThreshold = 60;

    /// <summary>GPU 解码器回落到该占用以下才可能解除压缩（%）。</summary>
    public const double HealthyDecoderThreshold = 55;

    /// <summary>连续高负载周期达到该次数后进入 15fps 紧凑模式（每周期 2 秒）。</summary>
    public const int PressureCyclesBeforeCompact = 2;

    /// <summary>进入紧凑模式后仍连续高负载的周期数，达到后再降一档画质。</summary>
    public const int PressureCyclesBeforeQualityDowngrade = 3;

    /// <summary>连续健康周期达到该次数后解除压缩（每周期 2 秒，即约 30 秒）。</summary>
    public const int HealthyCyclesBeforeRecovery = 15;

    /// <summary>负载档位：没有 GPU 采样时保持 <see cref="Unknown"/>，策略不主动降级。</summary>
    public enum LoadLevel
    {
        /// <summary>没有 GPU 指标（非 NVIDIA 或 nvidia-smi 不可用）。</summary>
        Unknown,
        /// <summary>指标回落到健康区间。</summary>
        Healthy,
        /// <summary>介于压力与健康之间，保持现状。</summary>
        Neutral,
        /// <summary>利用率或解码占用达到压力阈值。</summary>
        Pressure
    }

    public static LoadLevel Classify(GpuLoadSnapshot gpu)
    {
        if (!gpu.HasSample)
        {
            return LoadLevel.Unknown;
        }

        var utilization = gpu.Utilization;
        var decoder = gpu.Decoder;
        if (utilization >= PressureUtilizationThreshold || decoder >= PressureDecoderThreshold)
        {
            return LoadLevel.Pressure;
        }

        var utilizationHealthy = utilization is not double u || u <= HealthyUtilizationThreshold;
        var decoderHealthy = decoder is not double d || d <= HealthyDecoderThreshold;
        return utilizationHealthy && decoderHealthy ? LoadLevel.Healthy : LoadLevel.Neutral;
    }

    public static bool ShouldCompact(int pressureCycles) => pressureCycles >= PressureCyclesBeforeCompact;

    public static bool ShouldDowngradeQuality(int pressureCyclesAfterCompact)
        => pressureCyclesAfterCompact >= PressureCyclesBeforeQualityDowngrade;

    public static bool ShouldRecover(int healthyCycles) => healthyCycles >= HealthyCyclesBeforeRecovery;
}
