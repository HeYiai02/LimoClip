using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 自由画布的窗格命中判定与层级整理。
/// 关键约定：点击命中的窗格必须与「视觉上最上层」的窗格一致——渲染时先按 ZIndex 升序、
/// 同 ZIndex 按子项加入顺序；命中判定必须用同一规则反推，否则会出现「点小窗却操作了底层大窗」。
/// </summary>
public static class FreeformPaneHitTest
{
    /// <summary>普通（非置顶）窗格的 ZIndex 上限，与 <see cref="FreeformWorkspaceCanvas"/> 保持一致。</summary>
    public const int NormalZMax = 999;

    /// <summary>
    /// 找出画布坐标 <paramref name="x"/>, <paramref name="y"/> 处最上层的窗格；没有窗格命中时返回 null。
    /// </summary>
    /// <param name="placements">画布上全部放置项。</param>
    /// <param name="visualOrder">返回窗格子项在画布 Children 中的下标（越大越晚加入）；未知窗格返回 -1。</param>
    public static FreeformTilePlacement? FindTopmost(
        IEnumerable<FreeformTilePlacement> placements,
        Func<string, int> visualOrder,
        double canvasWidth,
        double canvasHeight,
        double x,
        double y)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(visualOrder);

        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return null;
        }

        foreach (var placement in OrderTopmostFirst(placements, visualOrder))
        {
            var (left, top, width, height) = placement.ToRect().ToPixels(canvasWidth, canvasHeight);
            if (x >= left && x <= left + width && y >= top && y <= top + height)
            {
                return placement;
            }
        }

        return null;
    }

    /// <summary>
    /// 按「最上层在前」排序：先比 EffectiveZIndex，相同层级时后加入画布的子项在上（与渲染顺序一致）。
    /// </summary>
    public static IReadOnlyList<FreeformTilePlacement> OrderTopmostFirst(
        IEnumerable<FreeformTilePlacement> placements,
        Func<string, int> visualOrder)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(visualOrder);

        return placements
            .OrderByDescending(placement => placement.EffectiveZIndex)
            .ThenByDescending(placement => visualOrder(placement.PaneId))
            .ToList();
    }

    /// <summary>
    /// 普通（非置顶）窗格可用的下一个 ZIndex：现有普通层最大 ZIndex + 1；已到上限时从 1 重新开始。
    /// 新拖入画布的窗格用它可以保证层级唯一，立即成为可点击的最上层。
    /// </summary>
    public static int NextNormalZIndex(IEnumerable<FreeformTilePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var maxZ = placements
            .Where(item => !item.IsPinned)
            .Select(item => item.ZIndex)
            .DefaultIfEmpty(0)
            .Max();

        return maxZ >= NormalZMax ? 1 : maxZ + 1;
    }

    /// <summary>
    /// 把窗格的 ZIndex 压成组内唯一序列，消除历史布局文档或兜底落位产生的重复层级。
    /// 保持原有相对堆叠顺序：先按当前 ZIndex，再按加入画布顺序（后加入的在上）；置顶层与普通层各自独立编号。
    /// </summary>
    public static void NormalizeZIndexes(
        IEnumerable<FreeformTilePlacement> placements,
        Func<string, int> visualOrder)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(visualOrder);

        foreach (var group in placements.GroupBy(item => item.IsPinned))
        {
            var ordered = group
                .OrderBy(item => item.ZIndex)
                .ThenBy(item => visualOrder(item.PaneId))
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].ZIndex = index + 1;
            }
        }
    }
}
