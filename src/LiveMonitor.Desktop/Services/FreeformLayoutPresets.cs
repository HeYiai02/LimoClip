namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 自由画布的快捷布局生成器：「一键自适应」「主副屏」「画中画」都只是数学生成归一化矩形。
/// 生成之后用户可以继续拖拽/缩放微调，不存在模式割裂。
/// </summary>
public static class FreeformLayoutPresets
{
    /// <summary>画中画副窗默认尺寸（占画布宽高比例）。</summary>
    public const double DefaultPipSize = 0.28;

    /// <summary>主副屏布局中主画面的默认宽度比例。</summary>
    public const double DefaultHeroProportion = 0.72;

    /// <summary>新直播间在自由布局态下浮在画布中央的默认尺寸。</summary>
    public const double DefaultLandingSize = 0.35;

    private const int PinnedZBase = 1000;

    /// <summary>一键自适应：按行分布把画布均分为 count 个窗格（与旧平铺布局的观感一致）。</summary>
    public static List<FreeformTilePlacement> AutoFit(int count, double aspectRatio)
    {
        var safeCount = Math.Max(1, count);
        var rowCounts = CalculateRowCounts(safeCount, aspectRatio);
        var placements = new List<FreeformTilePlacement>(safeCount);
        var rowHeight = 1.0 / rowCounts.Length;
        var paneIndex = 0;

        for (var row = 0; row < rowCounts.Length; row++)
        {
            var columns = Math.Max(1, rowCounts[row]);
            var columnWidth = 1.0 / columns;
            var top = row * rowHeight;
            for (var column = 0; column < columns && paneIndex < safeCount; column++)
            {
                var rect = new NormalizedRect(column * columnWidth, top, columnWidth, rowHeight).ClampToCanvas();
                placements.Add(FreeformTilePlacement.Create($"pane-{paneIndex + 1}", rect, zIndex: paneIndex + 1));
                paneIndex++;
            }
        }

        return placements;
    }

    /// <summary>主副屏：第 1 路占主画面，其余在右侧纵向均分。</summary>
    public static List<FreeformTilePlacement> Hero(int count, double heroProportion = DefaultHeroProportion)
    {
        var safeCount = Math.Max(1, count);
        var heroWidth = Math.Clamp(heroProportion, 0.3, 0.95);
        var placements = new List<FreeformTilePlacement>(safeCount)
        {
            FreeformTilePlacement.Create("pane-1", new NormalizedRect(0, 0, heroWidth, 1), zIndex: 1)
        };

        if (safeCount == 1)
        {
            return placements;
        }

        var sideWidth = 1.0 - heroWidth;
        var sideCount = safeCount - 1;
        var sideHeight = 1.0 / sideCount;
        for (var index = 0; index < sideCount; index++)
        {
            var rect = new NormalizedRect(heroWidth, index * sideHeight, sideWidth, sideHeight).ClampToCanvas();
            placements.Add(FreeformTilePlacement.Create($"pane-{index + 2}", rect, zIndex: index + 2));
        }

        return placements;
    }

    /// <summary>
    /// 画中画：第 1 路铺满画布作为背景主画面，第 2~4 路缩到右下角并自动置顶（IsPinned=true）。
    /// 点底层大画面调音量/切画质不会把小窗盖住。
    /// </summary>
    public static List<FreeformTilePlacement> PictureInPicture(int count, double pipSize = DefaultPipSize)
    {
        var safeCount = Math.Max(1, count);
        var size = Math.Clamp(pipSize, 0.12, 0.5);
        var placements = new List<FreeformTilePlacement>(safeCount)
        {
            FreeformTilePlacement.Create("pane-1", NormalizedRect.FullCanvas, zIndex: 1)
        };

        if (safeCount == 1)
        {
            return placements;
        }

        const double margin = 0.015;
        const double gap = 0.01;
        var pipCount = safeCount - 1;
        for (var index = 0; index < pipCount; index++)
        {
            var top = 1.0 - size - margin - index * (size + gap);
            // 超过画布顶部时换列，避免叠出画布。
            var column = 0;
            while (top < margin && column < 2)
            {
                top += 1.0;
                column++;
            }

            top = Math.Max(margin, top);
            var left = 1.0 - size - margin - column * (size + gap);
            var rect = new NormalizedRect(left, top, size, size).ClampToCanvas();
            placements.Add(FreeformTilePlacement.Create(
                $"pane-{index + 2}",
                rect,
                zIndex: PinnedZBase + index + 1,
                isPinned: true));
        }

        return placements;
    }

    /// <summary>
    /// 新拖入直播间的默认落位：
    /// 自由布局态下不重排已有画面，浮在画布中央并轻微错开，等用户自己拖到目标位置磁吸。
    /// </summary>
    public static FreeformTilePlacement DefaultLanding(int existingCount, string paneId, Guid? liveRoomId = null)
    {
        var size = DefaultLandingSize;
        var cascade = Math.Abs(existingCount) % 5 * 0.03;
        var left = Math.Clamp(0.5 - size * 0.5 + cascade, 0.0, 1.0 - size);
        var top = Math.Clamp(0.5 - size * 0.5 + cascade, 0.0, 1.0 - size);
        var maxZ = 1;
        var rect = new NormalizedRect(left, top, size, size).ClampToCanvas();
        return FreeformTilePlacement.Create(paneId, rect, liveRoomId, zIndex: maxZ);
    }

    /// <summary>
    /// 旧版 Dock 方案迁移：按槽位数量生成一键自适应网格，不丢房间、不报错。
    /// </summary>
    public static FreeformLayoutDocument BuildMigratedDocument(IReadOnlyList<Guid?> liveRoomIds, double aspectRatio)
    {
        var count = Math.Max(1, liveRoomIds.Count);
        var placements = AutoFit(count, aspectRatio);
        for (var index = 0; index < placements.Count && index < liveRoomIds.Count; index++)
        {
            placements[index].LiveRoomId = liveRoomIds[index];
        }

        return FreeformLayoutDocument.FromPlacements(placements);
    }

    /// <summary>每行窗格数量（与旧平铺布局保持一致的行分布）。</summary>
    public static int[] CalculateRowCounts(int count, double aspectRatio)
    {
        var safeCount = Math.Max(1, count);
        var fixedRows = safeCount switch
        {
            1 => new[] { 1 },
            2 => new[] { 2 },
            3 => new[] { 1, 2 },
            4 => new[] { 2, 2 },
            5 => new[] { 3, 2 },
            6 => new[] { 3, 3 },
            7 => new[] { 3, 2, 2 },
            8 => new[] { 3, 3, 2 },
            9 => new[] { 3, 3, 3 },
            10 => new[] { 4, 3, 3 },
            11 => new[] { 4, 4, 3 },
            12 => new[] { 4, 4, 4 },
            _ => Array.Empty<int>()
        };
        if (fixedRows.Length > 0)
        {
            return fixedRows;
        }

        var ratio = aspectRatio > 0 ? aspectRatio : 16.0 / 9.0;
        var columns = (int)Math.Ceiling(Math.Sqrt(safeCount * ratio * 9.0 / 16.0));
        columns = Math.Clamp(columns, 1, safeCount);
        var rows = (int)Math.Ceiling(safeCount / (double)columns);
        var basePerRow = safeCount / rows;
        var extra = safeCount % rows;
        var rowCounts = new int[rows];
        for (var row = 0; row < rows; row++)
        {
            rowCounts[row] = basePerRow + (row < extra ? 1 : 0);
        }

        return rowCounts;
    }
}

/// <summary>自由画布一键预设类型。</summary>
public enum FreeformPreset
{
    /// <summary>自动平铺（按窗格数切行列）。</summary>
    AutoFit,

    /// <summary>主副屏：左侧主画面 + 右侧副屏纵列。</summary>
    Hero,

    /// <summary>画中画：首路铺满，其余置顶浮层。</summary>
    PictureInPicture
}
