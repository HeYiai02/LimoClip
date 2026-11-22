using LiveMonitor.Core.Entities;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 弹幕屏蔽词库。V0.1 只做配置 CRUD，不做实时匹配。
/// </summary>
public interface IBlocklistRepository
{
    Task<IReadOnlyList<DanmakuBlockKeyword>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(DanmakuBlockKeyword keyword, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<bool> ExistsAsync(string keyword, CancellationToken ct = default);
}
