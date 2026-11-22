using LiveMonitor.Core.Entities;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 窗口方案仓储。SaveAsync 必须在同一事务内写 profiles 与 slots。
/// </summary>
public interface ILayoutProfileRepository
{
    Task<IReadOnlyList<LayoutProfile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>返回方案及其全部槽位；不存在返回 null。</summary>
    Task<LayoutProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>插入或更新方案，并整体替换槽位集合。</summary>
    Task SaveAsync(LayoutProfile profile, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
