using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>主库列表行开播状态展示映射（`RoomListStatusText`）的直接单测。</summary>
public sealed class RoomListStatusTextTests
{
    [Theory]
    [InlineData(true, "直播中", "#3DDC97")]
    [InlineData(false, "未开播", "#5B6472")]
    [InlineData(null, "在线状态未知（双击放入工作区后检测）", "#3A414D")]
    public void Tip_And_Brush_MapEveryStatus(bool? isLive, string tip, string brush)
    {
        Assert.Equal(tip, RoomListStatusText.Tip(isLive));
        Assert.Equal(brush, RoomListStatusText.Brush(isLive));
    }

    [Fact]
    public void KnownTips_ReusePaneStatusWording()
    {
        Assert.Equal(RoomStatusText.Live, RoomListStatusText.Tip(true));
        Assert.Equal(RoomStatusText.Offline, RoomListStatusText.Tip(false));
    }

    [Fact]
    public void UnknownStatus_IsNotDisplayedAsLive()
    {
        Assert.NotEqual(RoomListStatusText.Tip(true), RoomListStatusText.Tip(null));
        Assert.NotEqual(RoomListStatusText.Brush(true), RoomListStatusText.Brush(null));
        Assert.Contains("未知", RoomListStatusText.Tip(null));
    }

    [Fact]
    public void Brushes_AreDistinctPerStatus()
    {
        var brushes = new[]
        {
            RoomListStatusText.Brush(true),
            RoomListStatusText.Brush(false),
            RoomListStatusText.Brush(null),
        };

        Assert.Equal(3, brushes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ItemViewModel_DelegatesToSharedMapping()
    {
        var room = new LiveRoom
        {
            Id = Guid.NewGuid(),
            DisplayNumber = 1,
            Platform = PlatformType.Bilibili,
            RoomId = "7734200",
            RawUrl = "https://live.bilibili.com/7734200",
            Alias = "测试房间",
            CreatedAt = DateTime.UtcNow,
        };
        var item = new RoomListItemViewModel(room);

        Assert.Equal(RoomListStatusText.Tip(null), item.StatusTip);
        Assert.Equal(RoomListStatusText.Brush(null), item.StatusBrush);

        item.SetLiveStatus(false);
        Assert.Equal(RoomListStatusText.Tip(false), item.StatusTip);
        Assert.Equal(RoomListStatusText.Brush(false), item.StatusBrush);

        item.SetLiveStatus(true);
        Assert.Equal(RoomStatusText.Live, item.StatusTip);
    }
}
