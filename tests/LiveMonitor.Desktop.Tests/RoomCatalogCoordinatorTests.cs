using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RoomCatalogCoordinatorTests
{
    private static RoomListItemViewModel Room(int number, string alias, string? groupTag, string roomId = "room-1")
        => new(new LiveRoom
        {
            DisplayNumber = number,
            Alias = alias,
            GroupTag = groupTag,
            RoomId = roomId,
            Platform = PlatformType.Bilibili
        });

    [Fact]
    public void Filter_WithoutCriteria_ReturnsAllRoomsInOriginalOrder()
    {
        var rooms = new[]
        {
            Room(1, "Alpha", "游戏", "a"),
            Room(2, "Beta", "娱乐", "b"),
            Room(3, "Gamma", null, "c")
        };

        var result = RoomCatalogCoordinator.Filter(rooms, string.Empty, RoomCatalogCoordinator.AllGroupLabel);

        Assert.Equal(rooms, result);
    }

    [Fact]
    public void Filter_ByGroup_IgnoresCase()
    {
        var rooms = new[]
        {
            Room(1, "Alpha", "Game", "a"),
            Room(2, "Beta", "娱乐", "b"),
            Room(3, "Gamma", "game", "c")
        };

        var result = RoomCatalogCoordinator.Filter(rooms, "  ", "GAME");

        Assert.Equal(new[] { rooms[0], rooms[2] }, result);
    }

    [Fact]
    public void Filter_TrimsSearchTextAndSearchesDisplayFields()
    {
        var rooms = new[]
        {
            Room(1, "Alpha", "娱乐", "cs_alpha"),
            Room(2, "Beta", "娱乐", "room-2")
        };

        var byAlias = RoomCatalogCoordinator.Filter(rooms, "  ALPHA  ", RoomCatalogCoordinator.AllGroupLabel);
        var byRoomId = RoomCatalogCoordinator.Filter(rooms, "ROOM-2", RoomCatalogCoordinator.AllGroupLabel);

        Assert.Equal(new[] { rooms[0] }, byAlias);
        Assert.Equal(new[] { rooms[1] }, byRoomId);
    }

    [Fact]
    public void Filter_CombinesGroupAndSearchCriteria()
    {
        var rooms = new[]
        {
            Room(1, "Alpha", "游戏", "a"),
            Room(2, "Alpha", "娱乐", "b")
        };

        var result = RoomCatalogCoordinator.Filter(rooms, "alpha", "游戏");

        Assert.Equal(new[] { rooms[0] }, result);
    }

    [Fact]
    public void BuildGroups_PutsAllFirstThenDistinctSortedTags()
    {
        var rooms = new[]
        {
            Room(1, "A", null),
            Room(2, "B", ""),
            Room(3, "C", "  "),
            Room(4, "D", "Game"),
            Room(5, "E", "game"),
            Room(6, "F", "娱乐"),
            Room(7, "G", "游戏")
        };

        var groups = RoomCatalogCoordinator.BuildGroups(rooms);

        Assert.Equal(new[] { "全部", "Game", "娱乐", "游戏" }, groups);
    }

    [Fact]
    public void IsKnownGroup_MatchesCaseInsensitively()
    {
        var groups = new[] { "全部", "Game" };

        Assert.True(RoomCatalogCoordinator.IsKnownGroup(groups, "GAME"));
        Assert.True(RoomCatalogCoordinator.IsKnownGroup(groups, RoomCatalogCoordinator.AllGroupLabel));
        Assert.False(RoomCatalogCoordinator.IsKnownGroup(groups, "不存在"));
        Assert.False(RoomCatalogCoordinator.IsKnownGroup(groups, null));
    }
}
