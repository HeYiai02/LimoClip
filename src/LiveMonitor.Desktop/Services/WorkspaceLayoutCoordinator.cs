using Dock.Model.Controls;
using Dock.Model.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 工作区布局相关的纯计算/编排：状态栏文案、自由画布文档生成、Dock 树遍历与
/// 方案槽位（LayoutSlot）构建。不持有布局实例、不触发通知，由 MainWindowViewModel 负责应用结果。
/// </summary>
internal static class WorkspaceLayoutCoordinator
{
    /// <summary>布局模式状态栏文案（自由布局 &gt; 主副屏 &gt; 自定义比例/自动适配）。</summary>
    public static string FormatLayoutModeText(bool isFreeformMode, bool isHeroLayout, bool isCustomLayout) =>
        isFreeformMode
            ? "自由布局"
            : isHeroLayout
                ? (isCustomLayout ? "主副屏 · 自定义" : "主副屏")
                : (isCustomLayout ? "自定义比例" : "自动适配");

    /// <summary>工作区窗格计数状态栏文案；主副屏模式额外显示副屏路数。</summary>
    public static string FormatRoomCountText(
        int libraryCount,
        bool isHeroLayout,
        int workspaceRoomCount,
        int gridRows,
        int gridColumns) =>
        isHeroLayout
            ? $"主库 {libraryCount} 路 · 工作区 主副屏（{workspaceRoomCount} 路）"
            : $"主库 {libraryCount} 路 · 工作区 {gridRows}×{gridColumns}";

    /// <summary>「独轮车已拦」状态栏文案。</summary>
    public static string FormatUnicycleBlockedText(int blockedUserCount) =>
        $"独轮车已拦: {blockedUserCount} 人";

    /// <summary>工作区中已绑定直播间的窗格数量。</summary>
    public static int CountOccupiedPanes(IRootDock? layout) =>
        WorkspaceDockBuilder.EnumerateTiles(layout).Count(tile => tile.HasRoom);

    /// <summary>
    /// 按预设生成自由画布文档：窗格按 PaneId 排序后依次套用预设矩形，
    /// 预设数量不足时用默认落位补足，已绑定房间的窗格带上 LiveRoomId。
    /// </summary>
    public static FreeformLayoutDocument BuildFreeformDocument(
        IReadOnlyList<RoomTileViewModel> tiles,
        FreeformPreset preset,
        double workspaceAspect)
    {
        var ordered = tiles.OrderBy(tile => tile.PaneId, StringComparer.Ordinal).ToList();
        var count = Math.Max(1, ordered.Count);
        var sources = preset switch
        {
            FreeformPreset.Hero => FreeformLayoutPresets.Hero(count),
            FreeformPreset.PictureInPicture => FreeformLayoutPresets.PictureInPicture(count),
            _ => FreeformLayoutPresets.AutoFit(count, workspaceAspect)
        };

        var document = new FreeformLayoutDocument();
        for (var index = 0; index < ordered.Count; index++)
        {
            var tile = ordered[index];
            var source = index < sources.Count
                ? sources[index]
                : FreeformLayoutPresets.DefaultLanding(index, tile.PaneId);
            document.Tiles.Add(FreeformTilePlacement.Create(
                tile.PaneId,
                source.ToRect(),
                tile.HasRoom ? tile.LiveRoomId : null,
                source.ZIndex,
                source.IsPinned,
                source.IsLocked));
        }

        return document;
    }

    /// <summary>深度优先遍历 Dock 树（含 root 自身），供布局属性变更跟踪订阅/退订。</summary>
    public static IEnumerable<IDockable> EnumerateDockables(IDockable root)
    {
        yield return root;
        if (root is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                foreach (var nested in EnumerateDockables(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>两套方案是否拥有完全相同的窗格集合（PaneId 排序后逐项比较）。</summary>
    public static bool HasSamePaneIds(IRootDock left, IRootDock right)
    {
        var leftIds = WorkspaceDockBuilder.EnumerateTiles(left)
            .Select(tile => tile.PaneId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var rightIds = WorkspaceDockBuilder.EnumerateTiles(right)
            .Select(tile => tile.PaneId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return leftIds.SequenceEqual(rightIds);
    }

    /// <summary>把已绑定房间的窗格转换成方案槽位；LayoutProfileId 由调用方决定（快照用 Guid.Empty）。</summary>
    public static List<LayoutSlot> CreateLayoutSlots(IEnumerable<RoomTileViewModel> tiles, Guid profileId) =>
        tiles
            .Where(tile => tile.HasRoom)
            .Select(tile => new LayoutSlot
            {
                LayoutProfileId = profileId,
                LiveRoomId = tile.LiveRoomId,
                PaneId = tile.PaneId,
                DefaultMuted = true,
                DefaultDanmakuVisible = tile.IsDanmakuVisible,
                IsFocusSlot = false,
                PreferredQualityId = tile.Status?.AvailableQualities.FirstOrDefault()?.Id
            })
            .ToList();
}
