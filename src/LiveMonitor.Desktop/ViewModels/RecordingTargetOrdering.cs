using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制目标列表排序规则：正在录制的直播间排最前，其次是正在直播的直播间，
/// 其余按显示序号；同一优先级内仍按显示序号升序，序号相同则保持原有相对顺序（稳定排序）。
/// </summary>
public static class RecordingTargetOrdering
{
    /// <summary>优先级数字越小越靠前：录制中(0) &gt; 直播中(1) &gt; 其他(2)。</summary>
    public static int Rank(bool isRecording, bool isLive)
        => isRecording ? 0 : isLive ? 1 : 2;

    /// <summary>按「录制中 &gt; 直播中 &gt; 其他，再按显示序号」稳定排序，返回新列表。</summary>
    public static IReadOnlyList<T> OrderByPriority<T>(
        IEnumerable<T> items,
        Func<T, bool> isRecording,
        Func<T, bool> isLive,
        Func<T, int> displayNumber)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isRecording);
        ArgumentNullException.ThrowIfNull(isLive);
        ArgumentNullException.ThrowIfNull(displayNumber);

        // OrderBy 是稳定排序：同优先级且同显示序号时保留进入列表时的相对顺序。
        return items
            .OrderBy(item => Rank(isRecording(item), isLive(item)))
            .ThenBy(displayNumber)
            .ToList();
    }
}
