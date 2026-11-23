namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 硬件编码器探测器：用 0.1 秒空输入实测 nvenc / qsv / amf 是否真正可用，
/// 全失败时回退 libx264，并在本次会话内缓存结果。
/// </summary>
public interface IHardwareEncoderDetector
{
    /// <summary>
    /// 返回当前机器可用的最佳 H.264 编码器及其推荐参数。
    /// </summary>
    Task<(string EncoderName, string[] ExtraArgs)> DetectBestAsync(CancellationToken ct = default);
}
