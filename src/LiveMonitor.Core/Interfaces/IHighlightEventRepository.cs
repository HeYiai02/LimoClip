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

    /// <summary>
    /// 返回绑定到指定录制会话的高能事件，按录制内偏移升序排列（V0.4 剪辑台高能推荐列表）。
    /// 未绑定会话（RecordingSessionId 为 null）的事件不会返回。
    /// </summary>
    Task<IReadOnlyList<HighlightEvent>> GetByRecordingSessionAsync(
        Guid sessionId,
        int limit = 500,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
