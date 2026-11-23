using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 高能事件落盘。应用重启后事件面板据此恢复历史。
/// </summary>
public interface IHighlightEventRepository
{
    Task AddAsync(HighlightEvent highlightEvent, CancellationToken ct = default);

    /// <summary>按 TriggerTime 倒序返回最近的事件。</summary>
    Task<IReadOnlyList<HighlightEvent>> GetRecentAsync(int limit = 200, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
