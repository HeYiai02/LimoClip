using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 直播间主库仓储。(Platform, RoomId) 全局唯一，DisplayNumber 全局递增且唯一。
/// </summary>
public interface IRoomRepository
{
    Task<IReadOnlyList<LiveRoom>> GetAllAsync(CancellationToken ct = default);

    Task<LiveRoom?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<bool> ExistsAsync(PlatformType platform, string roomId, CancellationToken ct = default);

    /// <summary>返回当前最大 DisplayNumber + 1；空库返回 1。</summary>
    Task<int> GetNextDisplayNumberAsync(CancellationToken ct = default);

    Task AddAsync(LiveRoom room, CancellationToken ct = default);

    Task UpdateAsync(LiveRoom room, CancellationToken ct = default);

    /// <summary>删除直播间；关联的 LayoutSlots 由外键级联删除。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
