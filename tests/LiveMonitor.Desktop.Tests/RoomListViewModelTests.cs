using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RoomListViewModelTests
{
    [Theory]
    [InlineData(PlatformType.Bilibili, "#FB7299", "#FFFFFF")]
    [InlineData(PlatformType.Twitch, "#9146FF", "#FFFFFF")]
    [InlineData(PlatformType.Douyin, "#FE2C55", "#FFFFFF")]
    [InlineData(PlatformType.Douyu, "#FF6A00", "#FFFFFF")]
    [InlineData(PlatformType.Huya, "#FFB300", "#1A1A1A")]
    [InlineData(PlatformType.YouTube, "#FF0033", "#FFFFFF")]
    public void Badge_uses_platform_specific_colors(
        PlatformType platform,
        string expectedBackground,
        string expectedForeground)
    {
        var item = new RoomListItemViewModel(CreateRoom(platform));

        Assert.Equal(expectedBackground, item.PlatformBadgeBackground);
        Assert.Equal(expectedForeground, item.PlatformBadgeForeground);
    }

    [Fact]
    public void Row_metadata_includes_display_number_and_group()
    {
        var room = CreateRoom(PlatformType.Bilibili);
        room.DisplayNumber = 7;
        room.Alias = "晚风";
        room.GroupTag = "游戏";

        var item = new RoomListItemViewModel(room);

        Assert.Equal("#7 晚风", item.DisplayTitle);
        Assert.Equal("游戏 · 7734200", item.Meta);
    }

    [Fact]
    public void Status_dot_starts_unknown_and_reflects_explicit_status()
    {
        var item = new RoomListItemViewModel(CreateRoom(PlatformType.Bilibili));

        Assert.Equal("#3A414D", item.StatusBrush);
        Assert.Contains("未知", item.StatusTip);

        item.SetLiveStatus(true);
        Assert.Equal("#3DDC97", item.StatusBrush);
        Assert.Equal("直播中", item.StatusTip);

        item.SetLiveStatus(false);
        Assert.Equal("#5B6472", item.StatusBrush);
        Assert.Equal("未开播", item.StatusTip);
    }

    [Fact]
    public void UpdateMetadata_refreshes_title_without_touching_room_identity()
    {
        var original = CreateRoom(PlatformType.Bilibili);
        original.DisplayNumber = 1;
        original.Alias = "旧名";
        var tile = new RoomTileViewModel("pane-1", original);

        var renamed = CreateRoom(PlatformType.Bilibili);
        renamed.Id = original.Id;
        renamed.DisplayNumber = 3;
        renamed.Alias = "新名";
        tile.UpdateMetadata(renamed);

        Assert.Equal(original.Id, tile.LiveRoomId);
        Assert.Equal("#3 新名", tile.DisplayTitle);
    }

    private static LiveRoom CreateRoom(PlatformType platform) => new()
    {
        Id = Guid.NewGuid(),
        DisplayNumber = 1,
        Platform = platform,
        RoomId = "7734200",
        RawUrl = "https://live.bilibili.com/7734200",
        Alias = "测试主播",
        CreatedAt = DateTime.UtcNow
    };
}
