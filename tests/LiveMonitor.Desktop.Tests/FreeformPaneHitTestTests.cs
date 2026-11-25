using System;
using System.Collections.Generic;
using System.Linq;
using LiveMonitor.Desktop.Controls;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class FreeformPaneHitTestTests
{
    private static Func<string, int> Order(params string[] paneIds)
    {
        var map = paneIds
            .Select((paneId, index) => (paneId, index))
            .ToDictionary(pair => pair.paneId, pair => pair.index, StringComparer.Ordinal);
        return paneId => map.TryGetValue(paneId, out var index) ? index : -1;
    }

    private static FreeformTilePlacement Tile(
        string paneId,
        NormalizedRect rect,
        int zIndex = 1,
        bool isPinned = false)
        => FreeformTilePlacement.Create(paneId, rect, zIndex: zIndex, isPinned: isPinned);

    [Fact]
    public void Small_tile_above_full_canvas_tile_at_same_z_receives_click()
    {
        // 复现场景：自由布局里只有 1 路（铺满画布，z=1），随后从左侧拖入第 2 路，
        // 旧逻辑给新窗格兜底 ZIndex=1 → 与背景大窗同层。渲染上小窗（后加入）在上，
        // 点击却按字典顺序选中背景大窗，表现为「小窗左键/右键都点不动」。
        var background = Tile("pane-4", NormalizedRect.FullCanvas, zIndex: 1);
        var small = Tile("pane-7", new NormalizedRect(0.58, 0.52, 0.4, 0.44), zIndex: 1);
        var visualOrder = Order("pane-4", "pane-7");

        var hit = FreeformPaneHitTest.FindTopmost(
            new[] { background, small },
            visualOrder,
            canvasWidth: 1160,
            canvasHeight: 900,
            x: 0.7 * 1160,
            y: 0.7 * 900);

        Assert.Same(small, hit);
    }

    [Fact]
    public void Point_outside_small_tile_hits_background_tile()
    {
        var background = Tile("pane-4", NormalizedRect.FullCanvas, zIndex: 1);
        var small = Tile("pane-7", new NormalizedRect(0.58, 0.52, 0.4, 0.44), zIndex: 2);

        var hit = FreeformPaneHitTest.FindTopmost(
            new[] { background, small },
            Order("pane-4", "pane-7"),
            1160,
            900,
            x: 0.2 * 1160,
            y: 0.2 * 900);

        Assert.Same(background, hit);
    }

    [Fact]
    public void Higher_effective_z_wins_over_later_add_order()
    {
        var big = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: 2);
        var small = Tile("pane-b", new NormalizedRect(0.5, 0.5, 0.4, 0.4), zIndex: 1);

        var hit = FreeformPaneHitTest.FindTopmost(
            new[] { big, small },
            Order("pane-a", "pane-b"),
            1000,
            600,
            x: 600,
            y: 400);

        Assert.Same(big, hit);
    }

    [Fact]
    public void Pinned_tile_wins_over_normal_tile()
    {
        var background = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: 5);
        var pinned = Tile("pane-b", new NormalizedRect(0.5, 0.5, 0.4, 0.4), zIndex: 1, isPinned: true);

        var hit = FreeformPaneHitTest.FindTopmost(
            new[] { background, pinned },
            Order("pane-a", "pane-b"),
            1000,
            600,
            x: 600,
            y: 400);

        Assert.Same(pinned, hit);
    }

    [Fact]
    public void FindTopmost_returns_null_outside_all_placements()
    {
        var small = Tile("pane-a", new NormalizedRect(0.5, 0.5, 0.2, 0.2));

        var hit = FreeformPaneHitTest.FindTopmost(
            new[] { small },
            Order("pane-a"),
            1000,
            600,
            x: 10,
            y: 10);

        Assert.Null(hit);
    }

    [Fact]
    public void NextNormalZIndex_starts_at_one_when_canvas_empty()
    {
        Assert.Equal(1, FreeformPaneHitTest.NextNormalZIndex(Array.Empty<FreeformTilePlacement>()));
    }

    [Fact]
    public void NextNormalZIndex_ignores_pinned_layer()
    {
        var normal = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: 3);
        var pinned = Tile("pane-b", new NormalizedRect(0, 0, 0.2, 0.2), zIndex: 50, isPinned: true);

        Assert.Equal(4, FreeformPaneHitTest.NextNormalZIndex(new[] { normal, pinned }));
    }

    [Fact]
    public void NextNormalZIndex_wraps_when_normal_layer_is_full()
    {
        var topmost = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: FreeformPaneHitTest.NormalZMax);

        Assert.Equal(1, FreeformPaneHitTest.NextNormalZIndex(new[] { topmost }));
    }

    [Fact]
    public void NormalizeZIndexes_breaks_same_level_tie_by_add_order()
    {
        var background = Tile("pane-4", NormalizedRect.FullCanvas, zIndex: 1);
        var small = Tile("pane-7", new NormalizedRect(0.58, 0.52, 0.4, 0.44), zIndex: 1);

        FreeformPaneHitTest.NormalizeZIndexes(new[] { background, small }, Order("pane-4", "pane-7"));

        Assert.Equal(1, background.ZIndex);
        Assert.Equal(2, small.ZIndex);
    }

    [Fact]
    public void NormalizeZIndexes_keeps_relative_stacking_order()
    {
        var a = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: 3);
        var b = Tile("pane-b", new NormalizedRect(0, 0, 0.2, 0.2), zIndex: 1);
        var c = Tile("pane-c", new NormalizedRect(0.2, 0, 0.2, 0.2), zIndex: 2);

        FreeformPaneHitTest.NormalizeZIndexes(new[] { a, b, c }, Order("pane-a", "pane-b", "pane-c"));

        Assert.Equal(1, b.ZIndex);
        Assert.Equal(2, c.ZIndex);
        Assert.Equal(3, a.ZIndex);
    }

    [Fact]
    public void NormalizeZIndexes_numbers_pinned_layer_independently()
    {
        var normalA = Tile("pane-a", NormalizedRect.FullCanvas, zIndex: 1);
        var normalB = Tile("pane-b", new NormalizedRect(0, 0, 0.2, 0.2), zIndex: 1);
        var pinnedA = Tile("pane-c", new NormalizedRect(0.5, 0.5, 0.2, 0.2), zIndex: 3, isPinned: true);
        var pinnedB = Tile("pane-d", new NormalizedRect(0.6, 0.6, 0.2, 0.2), zIndex: 3, isPinned: true);

        FreeformPaneHitTest.NormalizeZIndexes(
            new[] { normalA, normalB, pinnedA, pinnedB },
            Order("pane-a", "pane-b", "pane-c", "pane-d"));

        Assert.Equal(1, normalA.ZIndex);
        Assert.Equal(2, normalB.ZIndex);
        Assert.Equal(1, pinnedA.ZIndex);
        Assert.Equal(2, pinnedB.ZIndex);
    }

    [Fact]
    public void OrderTopmostFirst_sorts_by_effective_z_then_reverse_add_order()
    {
        var background = Tile("pane-4", NormalizedRect.FullCanvas, zIndex: 1);
        var small = Tile("pane-7", new NormalizedRect(0.58, 0.52, 0.4, 0.44), zIndex: 2);
        var pinned = Tile("pane-9", new NormalizedRect(0, 0, 0.2, 0.2), zIndex: 1, isPinned: true);

        var ordered = FreeformPaneHitTest
            .OrderTopmostFirst(new[] { background, small, pinned }, Order("pane-4", "pane-7", "pane-9"))
            .Select(item => item.PaneId)
            .ToArray();

        Assert.Equal(new[] { "pane-9", "pane-7", "pane-4" }, ordered);
    }
}
