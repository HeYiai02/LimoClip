using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 生成平铺式无边框工作区。每个格子的 PaneId 固定为 pane-{n}，同时作为 Tool.Id，供 LayoutSlots 绑定。
/// </summary>
public sealed class WorkspaceDockBuilder : Factory
{
    public IRootDock CreateGrid(int rows, int cols, Func<string, RoomTileViewModel> tileFactory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentNullException.ThrowIfNull(tileFactory);

        return CreateRows(Enumerable.Repeat(cols, rows).ToArray(), tileFactory);
    }

    /// <summary>
    /// 按数量与工作区宽高比自动排布：cols = ceil(sqrt(N * (W/H) * 9/16))。
    /// 行内数量尽量均分，最后一行不足时自动占满宽度。
    /// </summary>
    public IRootDock CreateAutoFit(int count, double aspectRatio, Func<string, RoomTileViewModel> tileFactory)
    {
        ArgumentNullException.ThrowIfNull(tileFactory);

        var rowCounts = CalculateAutoFitRowCounts(count, aspectRatio);
        return CreateRows(rowCounts, tileFactory);
    }

    /// <summary>
    /// 每行的窗格数量。3 路特意排成“上 1 下 2”，避免 2×2 留一个空窗格；
    /// 7~12 路同样使用不均衡行，保持常见监控墙的紧凑形状。
    /// </summary>
    public static int[] CalculateAutoFitRowCounts(int count, double aspectRatio)
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

    /// <summary>返回自动排布的行列数；不生成布局，便于测试与状态栏显示。</summary>
    public static (int Rows, int Columns) CalculateAutoFitGrid(int count, double aspectRatio)
    {
        var rowCounts = CalculateAutoFitRowCounts(count, aspectRatio);
        return (rowCounts.Length, rowCounts.Max());
    }

    private IRootDock CreateRows(IReadOnlyList<int> rowCounts, Func<string, RoomTileViewModel> tileFactory)
    {
        var root = CreateRootDock();
        root.Id = "workspace-root";

        var outer = new ProportionalDock
        {
            Id = "workspace-grid",
            Orientation = Dock.Model.Core.Orientation.Vertical,
            IsCollapsable = false,
            Proportion = 1
        };

        var rowDocks = new List<IDockable>();
        var number = 1;

        for (var rowIndex = 0; rowIndex < rowCounts.Count; rowIndex++)
        {
            var row = new ProportionalDock
            {
                Id = $"workspace-row-{rowIndex}",
                Orientation = Dock.Model.Core.Orientation.Horizontal,
                IsCollapsable = false,
                Proportion = 1
            };

            var panes = new List<IDockable>();
            for (var colIndex = 0; colIndex < rowCounts[rowIndex]; colIndex++)
            {
                if (panes.Count > 0)
                {
                    panes.Add(CreateProportionalDockSplitter());
                }

                var paneId = $"pane-{number++}";
                panes.Add(CreateToolDock(tileFactory(paneId)));
            }

            row.VisibleDockables = CreateList<IDockable>(panes.ToArray());
            row.ActiveDockable = panes.FirstOrDefault(d => d is ToolDock);
            rowDocks.Add(row);
            if (rowIndex < rowCounts.Count - 1)
            {
                rowDocks.Add(CreateProportionalDockSplitter());
            }
        }

        outer.VisibleDockables = CreateList<IDockable>(rowDocks.ToArray());
        outer.ActiveDockable = rowDocks.FirstOrDefault(d => d is ProportionalDock);

        root.VisibleDockables = CreateList<IDockable>(outer);
        root.DefaultDockable = outer;
        return root;
    }

    /// <summary>
    /// 主副屏布局：左侧一路主画面（pane-1），右侧一列纵向排列剩余窗格（pane-2..N）。
    /// 适合“一路重点关注 + 其余扫视”的观看习惯；结构与普通网格同为 ToolDock/ProportionalDock，
    /// 保存/恢复比例/拖拽逻辑完全复用，仅比例不同。
    /// </summary>
    public IRootDock CreateHeroAndSideColumn(
        int count,
        Func<string, RoomTileViewModel> tileFactory,
        double heroProportion = 0.7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentNullException.ThrowIfNull(tileFactory);

        // 1~2 路没有“主副”意义，退化成单行，避免一块占满宽度的竖直侧栏。
        if (count <= 2)
        {
            return CreateRows(new[] { count }, tileFactory);
        }

        var root = CreateRootDock();
        root.Id = "workspace-root";

        var hero = Math.Clamp(heroProportion, 0.3, 0.85);
        var outer = new ProportionalDock
        {
            Id = "workspace-hero",
            Orientation = Dock.Model.Core.Orientation.Horizontal,
            IsCollapsable = false,
            Proportion = 1
        };

        var heroDock = CreateToolDock(tileFactory("pane-1"));
        heroDock.Proportion = hero;

        var sideColumn = new ProportionalDock
        {
            Id = "workspace-side-column",
            Orientation = Dock.Model.Core.Orientation.Vertical,
            IsCollapsable = false,
            Proportion = 1 - hero
        };

        var sidePanes = new List<IDockable>();
        for (var index = 2; index <= count; index++)
        {
            if (sidePanes.Count > 0)
            {
                sidePanes.Add(CreateProportionalDockSplitter());
            }

            sidePanes.Add(CreateToolDock(tileFactory($"pane-{index}")));
        }

        sideColumn.VisibleDockables = CreateList<IDockable>(sidePanes.ToArray());
        sideColumn.ActiveDockable = sidePanes.FirstOrDefault(d => d is ToolDock);

        var outerChildren = new List<IDockable>
        {
            heroDock,
            CreateProportionalDockSplitter(),
            sideColumn
        };
        outer.VisibleDockables = CreateList<IDockable>(outerChildren.ToArray());
        outer.ActiveDockable = heroDock;

        root.VisibleDockables = CreateList<IDockable>(outer);
        root.DefaultDockable = outer;
        return root;
    }

    /// <summary>判断布局树的任意节点 Id 是否等于 <paramref name="id"/>（用于识别主副屏等特殊布局）。</summary>
    public static bool ContainsDockId(IRootDock? root, string id)
    {
        if (root is null || string.IsNullOrEmpty(id))
        {
            return false;
        }

        return ContainsDockIdCore(root, id);
    }

    private static bool ContainsDockIdCore(IDockable dockable, string id)
    {
        if (string.Equals(dockable.Id, id, StringComparison.Ordinal))
        {
            return true;
        }

        if (dockable is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                if (ContainsDockIdCore(child, id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 复制布局结构（保留 PaneId、Proportion、行列拆分），把所有 Tool.Context 换成新的窗格模型。
    /// 用于保存 DockLayoutJson：既不序列化 PlayerView 控件，也不破坏 3+1 / 3+2 等非矩形结构。
    /// extraTile 不为空时，追加到最后一个横排行（自定义比例下新增房间）。
    /// </summary>
    public IRootDock CloneStructure(
        IRootDock source,
        Func<string, RoomTileViewModel> tileFactory,
        RoomTileViewModel? extraTile = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tileFactory);

        var root = CreateRootDock();
        root.Id = source.Id;
        root.Proportion = source.Proportion;

        var rootChildren = new List<IDockable>();
        foreach (var child in source.VisibleDockables ?? Array.Empty<IDockable>())
        {
            rootChildren.Add(CloneDockable(child, tileFactory));
        }

        root.VisibleDockables = CreateList<IDockable>(rootChildren.ToArray());
        root.ActiveDockable = FindById(rootChildren, source.ActiveDockable?.Id);
        root.DefaultDockable = FindById(rootChildren, source.DefaultDockable?.Id);

        if (extraTile is not null)
        {
            _ = TryAppendToLastRow(root, extraTile);
        }

        return root;
    }

    /// <summary>
    /// 把 source 中保存的比例/折叠比例套回结构相同的 target，控件和播放器都不移动。
    /// 返回 false 表示树结构或 PaneId 不一致，调用方应走完整重建。
    /// </summary>
    public static bool TryApplyProportions(IRootDock target, IRootDock source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        return TryApplyDock(target, source);
    }

    /// <summary>
    /// 不重建 Dock 树，只把现有树里的比例恢复成均分。
    /// 一键自适应在行列数不变时走这里，播放器和音频状态完全不动。
    /// </summary>
    public static void EnableDragging(IDockable? root)
    {
        if (root is null)
        {
            return;
        }

        if (root is not IProportionalDockSplitter)
        {
            root.CanDrag = true;
            root.CanDrop = true;
        }

        if (root is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                EnableDragging(child);
            }
        }
    }

    public static bool ResetProportions(IRootDock root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return ResetDock(root);
    }

    private static bool ResetDock(IDock dock)
    {
        var children = dock.VisibleDockables;
        if (children is null)
        {
            return false;
        }

        foreach (var child in children)
        {
            if (child is IProportionalDockSplitter)
            {
                continue;
            }

            child.Proportion = 1;
            if (child is IDock childDock && !ResetDock(childDock))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyDock(IDock target, IDock source)
    {
        var targetChildren = target.VisibleDockables ?? Array.Empty<IDockable>();
        var sourceChildren = source.VisibleDockables ?? Array.Empty<IDockable>();
        if (targetChildren.Count != sourceChildren.Count)
        {
            return false;
        }

        for (var i = 0; i < targetChildren.Count; i++)
        {
            var targetChild = targetChildren[i];
            var sourceChild = sourceChildren[i];

            var targetIsSplitter = targetChild is IProportionalDockSplitter;
            var sourceIsSplitter = sourceChild is IProportionalDockSplitter;
            if (targetIsSplitter || sourceIsSplitter)
            {
                if (targetIsSplitter != sourceIsSplitter)
                {
                    return false;
                }

                continue;
            }

            if (targetChild.GetType() != sourceChild.GetType())
            {
                return false;
            }

            if (targetChild is Tool targetTool && sourceChild is Tool sourceTool)
            {
                if (!string.Equals(targetTool.Id, sourceTool.Id, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            targetChild.Proportion = sourceChild.Proportion;
            if (targetChild is IProportionalDock targetProportional &&
                sourceChild is IProportionalDock sourceProportional)
            {
                if (targetProportional.Orientation != sourceProportional.Orientation)
                {
                    return false;
                }
            }

            if (targetChild is IDock targetDock && sourceChild is IDock sourceDock)
            {
                if (!TryApplyDock(targetDock, sourceDock))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private IDockable CloneDockable(IDockable source, Func<string, RoomTileViewModel> tileFactory)
    {
        if (source is IProportionalDockSplitter)
        {
            return CreateProportionalDockSplitter();
        }

        if (source is Tool tool)
        {
            var tile = tileFactory(tool.Id);
            return new Tool
            {
                Id = tool.Id,
                Title = tile.HasRoom ? tile.DisplayTitle : $"窗格 {tool.Id}",
                Context = tile,
                Proportion = tool.Proportion,
                CanClose = false,
                CanFloat = false,
                CanDrag = true,
                CanDrop = true
            };
        }

        if (source is ToolDock toolDock)
        {
            var children = new List<IDockable>();
            foreach (var child in toolDock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                children.Add(CloneDockable(child, tileFactory));
            }

            return new ToolDock
            {
                Id = toolDock.Id,
                Proportion = toolDock.Proportion,
                IsCollapsable = toolDock.IsCollapsable,
                VisibleDockables = new List<IDockable>(children),
                ActiveDockable = FindById(children, toolDock.ActiveDockable?.Id),
                CanClose = false,
                CanFloat = false
            };
        }

        if (source is ProportionalDock proportional)
        {
            var children = new List<IDockable>();
            foreach (var child in proportional.VisibleDockables ?? Array.Empty<IDockable>())
            {
                children.Add(CloneDockable(child, tileFactory));
            }

            return new ProportionalDock
            {
                Id = proportional.Id,
                Orientation = proportional.Orientation,
                Proportion = proportional.Proportion,
                IsCollapsable = proportional.IsCollapsable,
                VisibleDockables = new List<IDockable>(children),
                ActiveDockable = FindById(children, proportional.ActiveDockable?.Id)
            };
        }

        if (source is IDock dock)
        {
            var children = new List<IDockable>();
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                children.Add(CloneDockable(child, tileFactory));
            }

            return new ProportionalDock
            {
                Id = dock.Id,
                Proportion = dock.Proportion,
                IsCollapsable = dock.IsCollapsable,
                VisibleDockables = new List<IDockable>(children),
                ActiveDockable = FindById(children, dock.ActiveDockable?.Id)
            };
        }

        throw new NotSupportedException($"不支持的 Dock 节点：{source.GetType().Name}");
    }

    private bool TryAppendToLastRow(IDockable root, RoomTileViewModel tile)
    {
        var rows = new List<ProportionalDock>();
        CollectRows(root, rows);
        if (rows.Count == 0)
        {
            return false;
        }

        var lastRow = rows[^1];
        lastRow.VisibleDockables ??= new List<IDockable>();
        if (lastRow.VisibleDockables.Count > 0)
        {
            lastRow.VisibleDockables.Add(CreateProportionalDockSplitter());
        }

        lastRow.VisibleDockables.Add(CreateToolDock(tile));
        return true;
    }

    private static void CollectRows(IDockable node, List<ProportionalDock> rows)
    {
        if (node is ProportionalDock proportional &&
            (proportional.VisibleDockables?.Any(child => child is ToolDock) ?? false))
        {
            rows.Add(proportional);
        }

        if (node is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                CollectRows(child, rows);
            }
        }
    }

    /// <summary>
    /// 从布局模型中移除指定 pane 的 ToolDock，并清理空行与多余 splitter。
    /// 返回是否实际移除；若移除后布局将不再有任何窗格则返回 false（保持原布局，由调用方决定重建）。
    /// </summary>
    public static bool RemovePane(IRootDock? layout, string paneId)
    {
        if (layout?.VisibleDockables is null || string.IsNullOrWhiteSpace(paneId))
        {
            return false;
        }

        var outer = layout.VisibleDockables.OfType<ProportionalDock>().FirstOrDefault();
        if (outer?.VisibleDockables is null)
        {
            return false;
        }

        ProportionalDock? ownerRow = null;
        ToolDock? target = null;
        foreach (var row in outer.VisibleDockables.OfType<ProportionalDock>())
        {
            target = row.VisibleDockables?.OfType<ToolDock>().FirstOrDefault(pane =>
                string.Equals(pane.Id, $"dock-{paneId}", StringComparison.Ordinal)
                || (pane.VisibleDockables?.OfType<Tool>().Any(tool =>
                        string.Equals(tool.Id, paneId, StringComparison.Ordinal)) ?? false));
            if (target is not null)
            {
                ownerRow = row;
                break;
            }
        }

        if (ownerRow?.VisibleDockables is null || target is null)
        {
            return false;
        }

        var rowChildren = ownerRow.VisibleDockables;
        var paneIndex = rowChildren.IndexOf(target);
        if (paneIndex < 0)
        {
            return false;
        }

        rowChildren.RemoveAt(paneIndex);
        TrimSplitters(rowChildren);

        if (!rowChildren.OfType<ToolDock>().Any())
        {
            var rowIndex = outer.VisibleDockables.IndexOf(ownerRow);
            if (rowIndex >= 0)
            {
                outer.VisibleDockables.RemoveAt(rowIndex);
                TrimSplitters(outer.VisibleDockables);
            }
        }

        var remainingRows = outer.VisibleDockables.OfType<ProportionalDock>()
            .Where(row => row.VisibleDockables?.OfType<ToolDock>().Any() == true)
            .ToList();
        if (remainingRows.Count == 0)
        {
            // 最后一个窗格：不把布局清空，交由调用方决定重建（例如自动模式重排）。
            return false;
        }

        outer.ActiveDockable = remainingRows[0];
        layout.DefaultDockable = outer;
        return true;
    }

    private static void TrimSplitters(IList<IDockable> children)
    {
        while (children.Count > 0 && children[0] is IProportionalDockSplitter)
        {
            children.RemoveAt(0);
        }

        while (children.Count > 0 && children[^1] is IProportionalDockSplitter)
        {
            children.RemoveAt(children.Count - 1);
        }

        for (var i = children.Count - 1; i > 0; i--)
        {
            if (children[i] is IProportionalDockSplitter && children[i - 1] is IProportionalDockSplitter)
            {
                children.RemoveAt(i);
            }
        }
    }

    private static ToolDock CreateToolDock(RoomTileViewModel tile)
    {
        var tool = new Tool
        {
            Id = tile.PaneId,
            Title = tile.HasRoom ? tile.DisplayTitle : $"窗格 {tile.PaneId}",
            Context = tile,
            CanClose = false,
            CanFloat = false,
            CanDrag = true,
            CanDrop = true
        };

        return new ToolDock
        {
            Id = $"dock-{tile.PaneId}",
            VisibleDockables = new List<IDockable> { tool },
            CanDrag = true,
            CanDrop = true,
            ActiveDockable = tool,
            Proportion = 1,
            CanClose = false,
            CanFloat = false
        };
    }

    private static IDockable? FindById(IReadOnlyList<IDockable> children, string? id)
    {
        if (id is not null)
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (string.Equals(children[i].Id, id, StringComparison.Ordinal))
                {
                    return children[i];
                }
            }
        }

        return children.Count > 0 ? children[0] : null;
    }

    public override IRootDock CreateLayout() =>
        CreateGrid(2, 2, paneId => new RoomTileViewModel(paneId, null));

    /// <summary>
    /// 旧版本保存的 DockLayoutJson 没有分割线节点；加载时补上，保证默认即可拖拽。
    /// </summary>
    public void EnsureSplitters(IRootDock layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var dock in EnumerateProportionalDocks(layout))
        {
            EnsureSplittersInDock(dock);
        }
    }

    private static IEnumerable<ProportionalDock> EnumerateProportionalDocks(IDockable dockable)
    {
        if (dockable is ProportionalDock proportional)
        {
            yield return proportional;
        }

        if (dockable is not IDock dock)
        {
            yield break;
        }

        foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
        {
            foreach (var nested in EnumerateProportionalDocks(child))
            {
                yield return nested;
            }
        }
    }

    private void EnsureSplittersInDock(ProportionalDock dock)
    {
        var children = dock.VisibleDockables?.ToList() ?? new List<IDockable>();
        var repaired = new List<IDockable>(children.Count + 4);
        foreach (var child in children)
        {
            if (child is IProportionalDockSplitter)
            {
                if (repaired.Count > 0 && repaired[^1] is not IProportionalDockSplitter)
                {
                    repaired.Add(child);
                }

                continue;
            }

            if (repaired.Count > 0 && repaired[^1] is not IProportionalDockSplitter)
            {
                repaired.Add(CreateProportionalDockSplitter());
            }

            repaired.Add(child);
        }

        dock.VisibleDockables = CreateList<IDockable>(repaired.ToArray());
    }

    /// <summary>
    /// 反序列化后 Tool.Context 为空，按 PaneId 重新绑定运行时窗格数据。
    /// </summary>
    public static void BindTiles(IRootDock layout, Func<string, RoomTileViewModel> tileFactory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(tileFactory);

        BindDock(layout, tileFactory);
    }

    private static void BindDock(IDockable dockable, Func<string, RoomTileViewModel> tileFactory)
    {
        if (dockable is Tool tool)
        {
            var tile = tileFactory(tool.Id);
            tool.Context = tile;
            tool.Title = tile.HasRoom ? tile.DisplayTitle : $"窗格 {tool.Id}";
        }

        if (dockable is not IDock dock)
        {
            return;
        }

        var children = dock.VisibleDockables ?? Array.Empty<IDockable>();
        foreach (var child in children)
        {
            BindDock(child, tileFactory);
        }

        if (dock.ActiveDockable is null || !children.Contains(dock.ActiveDockable))
        {
            dock.ActiveDockable = children.FirstOrDefault(child => child is not IProportionalDockSplitter);
        }
    }

    public static (int Rows, int Columns) GetGridDimensions(IRootDock layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var outer = (layout.VisibleDockables ?? Array.Empty<IDockable>())
            .OfType<ProportionalDock>()
            .FirstOrDefault();
        if (outer is null)
        {
            return (1, 1);
        }

        var rows = (outer.VisibleDockables ?? Array.Empty<IDockable>())
            .OfType<ProportionalDock>()
            .ToList();
        if (rows.Count == 0)
        {
            return (1, 1);
        }

        var columns = rows.Max(row => (row.VisibleDockables ?? Array.Empty<IDockable>()).Count(d => d is ToolDock));
        return (rows.Count, Math.Max(1, columns));
    }

    public static IEnumerable<RoomTileViewModel> EnumerateTiles(IRootDock? layout)
    {
        if (layout is null)
        {
            yield break;
        }

        foreach (var tile in Enumerate(layout))
        {
            yield return tile;
        }
    }

    private static IEnumerable<RoomTileViewModel> Enumerate(IDockable dockable)
    {
        if (dockable is Tool { Context: RoomTileViewModel tile })
        {
            yield return tile;
        }

        if (dockable is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                foreach (var nested in Enumerate(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
