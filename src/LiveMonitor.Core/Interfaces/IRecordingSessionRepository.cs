using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 录制会话历史仓储。会话记录独立于直播间生命周期：删除直播间只会把
/// LiveRoomId 置空，历史记录与本地产出文件都保留，供重新添加后回看。
/// </summary>
public interface IRecordingSessionRepository
{
    Task AddAsync(RecordingSession session, CancellationToken ct = default);

    /// <summary>按 Id 整行更新（状态、时长、文件大小、计数、结束时间等），不存在则抛异常。</summary>
    Task UpdateAsync(RecordingSession session, CancellationToken ct = default);

    Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>最近会话，按开始时间倒序。</summary>
    Task<IReadOnlyList<RecordingSession>> GetRecentAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>某平台房间的历史会话（按平台 + 房间号匹配，直播间删除重建后仍可查）。</summary>
    Task<IReadOnlyList<RecordingSession>> GetByRoomAsync(
        PlatformType platform,
        string roomId,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>读取未正常收尾的会话（Starting/Recording/Stopping），用于启动时崩溃恢复。</summary>
    Task<IReadOnlyList<RecordingSession>> GetUnfinishedAsync(CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
