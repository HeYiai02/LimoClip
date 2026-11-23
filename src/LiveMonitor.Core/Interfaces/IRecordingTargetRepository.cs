using LiveMonitor.Core.Entities;

namespace LiveMonitor.Core.Interfaces;

/// <summary>录制偏好仓储：每个直播间至多一行（LiveRoomId 主键）。</summary>
public interface IRecordingTargetRepository
{
    Task<IReadOnlyList<RecordingTarget>> GetAllAsync(CancellationToken ct = default);

    Task<RecordingTarget?> GetAsync(Guid liveRoomId, CancellationToken ct = default);

    /// <summary>不存在则插入，存在则覆盖（按 LiveRoomId upsert）。</summary>
    Task UpsertAsync(RecordingTarget target, CancellationToken ct = default);

    /// <summary>删除直播间的录制偏好；直播间本身被删除时由外键级联清理。</summary>
    Task DeleteAsync(Guid liveRoomId, CancellationToken ct = default);
}
