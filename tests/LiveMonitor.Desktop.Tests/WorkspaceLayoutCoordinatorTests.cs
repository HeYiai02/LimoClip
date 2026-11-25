using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class WorkspaceLayoutCoordinatorTests
{
    private const double WideAspect = 16.0 / 9.0;

    private static RoomTileViewModel Tile(string paneId) => new(paneId, null);

    [Theory]
    [InlineData(true, false, false, "自由布局")]
    [InlineData(false, true, false, "主副屏")]
    [InlineData(false, true, true, "主副屏 · 自定义")]
    [InlineData(false, false, true, "自定义比例")]
    [InlineData(false, false, false, "自动适配")]
    public void FormatLayoutModeText_ReturnsExpectedText(
        bool isFreeformMode,
        bool isHeroLayout,
        bool isCustomLayout,
        string expected)
    {
        var text = WorkspaceLayoutCoordinator.FormatLayoutModeText(isFreeformMode, isHeroLayout, isCustomLayout);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void FormatRoomCountText_UsesGridOrHeroSummary()
    {
        Assert.Equal(
            "主库 3 路 · 工作区 2×3",
            WorkspaceLayoutCoordinator.FormatRoomCountText(3, false, 9, 2, 3));
        Assert.Equal(
            "主库 3 路 · 工作区 主副屏（5 路）",
            WorkspaceLayoutCoordinator.FormatRoomCountText(3, true, 5, 2, 2));
        Assert.Equal("独轮车已拦: 7 人", WorkspaceLayoutCoordinator.FormatUnicycleBlockedText(7));
    }

    [Fact]
    public void BuildFreeformDocument_OrdersPanesAndBindsRooms()
    {
        var liveRoomId = Guid.NewGuid();
        var tiles = new[]
        {
            new RoomTileViewModel("pane-2", null) { LiveRoomId = liveRoomId },
            new RoomTileViewModel("pane-1", null)
        };

        var document = WorkspaceLayoutCoordinator.BuildFreeformDocument(tiles, FreeformPreset.Hero, WideAspect);

        Assert.Equal(2, document.Tiles.Count);
        Assert.Equal("pane-1", document.Tiles[0].PaneId);
        Assert.Null(document.Tiles[0].LiveRoomId);
        Assert.Equal(1, document.Tiles[0].ZIndex);
        Assert.Equal("pane-2", document.Tiles[1].PaneId);
        Assert.Equal(liveRoomId, document.Tiles[1].LiveRoomId);
        Assert.Equal(2, document.Tiles[1].ZIndex);
    }

    [Fact]
    public void BuildFreeformDocument_EmptyPaneList_ProducesEmptyDocument()
    {
        var document = WorkspaceLayoutCoordinator.BuildFreeformDocument(
            Array.Empty<RoomTileViewModel>(),
            FreeformPreset.AutoFit,
            WideAspect);

        Assert.Empty(document.Tiles);
    }

    [Fact]
    public void CreateLayoutSlots_SkipsEmptyPanesAndCopiesDefaults()
    {
        var profileId = Guid.NewGuid();
        var roomA = Guid.NewGuid();
        var roomC = Guid.NewGuid();
        var status = new RoomStatus(
            "room-a",
            PlatformType.Bilibili,
            "标题",
            "主播",
            true,
            new[]
            {
                new StreamQuality(
                    "q1080",
                    "1080P",
                    1080,
                    60,
                    "https://example.test/stream",
                    new Dictionary<string, string>())
            });

        var tiles = new[]
        {
            new RoomTileViewModel("pane-1", null)
            {
                LiveRoomId = roomA,
                IsDanmakuVisible = true,
                Status = status
            },
            new RoomTileViewModel("pane-2", null),
            new RoomTileViewModel("pane-3", null) { LiveRoomId = roomC }
        };

        var slots = WorkspaceLayoutCoordinator.CreateLayoutSlots(tiles, profileId);

        Assert.Equal(2, slots.Count);
        Assert.Equal(profileId, slots[0].LayoutProfileId);
        Assert.Equal(roomA, slots[0].LiveRoomId);
        Assert.Equal("pane-1", slots[0].PaneId);
        Assert.True(slots[0].DefaultMuted);
        Assert.True(slots[0].DefaultDanmakuVisible);
        Assert.False(slots[0].IsFocusSlot);
        Assert.Equal("q1080", slots[0].PreferredQualityId);
        Assert.Equal(roomC, slots[1].LiveRoomId);
        Assert.Equal("pane-3", slots[1].PaneId);
        Assert.Null(slots[1].PreferredQualityId);
    }

    [Fact]
    public void HasSamePaneIds_ComparesSortedPaneIdSets()
    {
        var builder = new WorkspaceDockBuilder();
        var left = builder.CreateGrid(1, 2, Tile);
        var same = builder.CreateGrid(1, 2, Tile);
        var morePanes = builder.CreateGrid(1, 3, Tile);

        Assert.True(WorkspaceLayoutCoordinator.HasSamePaneIds(left, same));
        Assert.False(WorkspaceLayoutCoordinator.HasSamePaneIds(left, morePanes));
    }

    [Fact]
    public void EnumerateDockables_TraversesWholeDockTree()
    {
        var builder = new WorkspaceDockBuilder();
        var layout = builder.CreateGrid(2, 2, Tile);

        var dockables = WorkspaceLayoutCoordinator.EnumerateDockables(layout).ToList();

        Assert.Equal(15, dockables.Count);
        Assert.Same(layout, dockables[0]);
        Assert.Equal(4, dockables.OfType<ToolDock>().Count());
        Assert.Equal(4, dockables.OfType<Tool>().Count());
        Assert.Equal(3, dockables.OfType<ProportionalDock>().Count());
    }

    [Fact]
    public void CountOccupiedPanes_CountsOnlyBoundTiles()
    {
        var builder = new WorkspaceDockBuilder();
        var layout = builder.CreateGrid(1, 2, Tile);
        Assert.Equal(0, WorkspaceLayoutCoordinator.CountOccupiedPanes(layout));

        WorkspaceDockBuilder.EnumerateTiles(layout).First().LiveRoomId = Guid.NewGuid();
        Assert.Equal(1, WorkspaceLayoutCoordinator.CountOccupiedPanes(layout));
        Assert.Equal(0, WorkspaceLayoutCoordinator.CountOccupiedPanes(null));
    }
}
