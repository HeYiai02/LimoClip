namespace LiveMonitor.Desktop.Services;

/// <summary>一次方案切换的差量结果：按 LiveRoomId 计算。</summary>
public sealed record LayoutDiffPlan(
    IReadOnlyList<Guid> Removed,
    IReadOnlyList<Guid> Retained,
    IReadOnlyList<Guid> Added)
{
    public bool HasChanges => Removed.Count > 0 || Added.Count > 0;
}

/// <summary>
/// 计算两个窗口方案之间的房间差量。保留集合的播放器不断流，只重新挂载到新窗格。
/// </summary>
public static class LayoutDiffService
{
    public static LayoutDiffPlan Plan(IEnumerable<Guid> previousRooms, IEnumerable<Guid> nextRooms)
    {
        ArgumentNullException.ThrowIfNull(previousRooms);
        ArgumentNullException.ThrowIfNull(nextRooms);

        var previous = previousRooms.Where(id => id != Guid.Empty).Distinct().ToHashSet();
        var next = nextRooms.Where(id => id != Guid.Empty).Distinct().ToHashSet();

        return new LayoutDiffPlan(
            previous.Except(next).ToArray(),
            previous.Intersect(next).ToArray(),
            next.Except(previous).ToArray());
    }
}
