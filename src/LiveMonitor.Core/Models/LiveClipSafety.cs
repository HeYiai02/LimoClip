namespace LiveMonitor.Core.Models;

/// <summary>
/// 「边录边剪」的安全边界计算：录制仍在写盘时，文件尾部可能尚未落盘或索引未收尾，
/// 预览拖动、选区出点与导出都必须留在安全时长之内，避免撞上半截包或触发录制端异常。
/// </summary>
public static class LiveClipSafety
{
    /// <summary>录制尾部保留的安全缓冲（5 秒）。</summary>
    public static readonly TimeSpan LiveEdgeSafetyMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 把录制中的实时时长换算为可安全播放 / 剪辑 / 导出的时长；
    /// 实时时长不足一个安全缓冲时返回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    /// <param name="liveRecordedDuration">录制引擎报告的已录制实时时长。</param>
    public static TimeSpan ComputeSafeDuration(TimeSpan liveRecordedDuration)
    {
        if (liveRecordedDuration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (liveRecordedDuration <= LiveEdgeSafetyMargin)
        {
            return TimeSpan.Zero;
        }

        return liveRecordedDuration - LiveEdgeSafetyMargin;
    }
}
