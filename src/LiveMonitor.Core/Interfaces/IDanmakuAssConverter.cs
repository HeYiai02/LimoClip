using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>把录制伴随弹幕的指定区间转换为可烧录的 .ass 字幕文件。</summary>
public interface IDanmakuAssConverter
{
    /// <summary>
    /// 将 <paramref name="allDanmaku"/> 中落在 [start, end] 的弹幕转换为 .ass 文件，
    /// 字幕时间轴从 0 开始（与切片起点对齐）。
    /// </summary>
    /// <returns>实际写入的弹幕条数。</returns>
    Task<int> ConvertToAssAsync(
        IReadOnlyList<RecordedDanmakuItem> allDanmaku,
        TimeSpan start,
        TimeSpan end,
        string outputAssPath,
        DanmakuAssStyle? style = null,
        CancellationToken ct = default);
}
