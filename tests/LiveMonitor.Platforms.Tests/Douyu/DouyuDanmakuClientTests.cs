using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuDanmakuClientTests
{
    [Fact]
    public async Task Constructor_NormalizesDouyuLinkToRoomId()
    {
        await using var client = new DouyuDanmakuClient("https://www.douyu.com/9999");
        Assert.Equal("9999", client.RoomId);
        Assert.Equal(PlatformType.Douyu, client.Platform);
    }

    [Fact]
    public async Task Constructor_AcceptsNumericRoomId()
    {
        await using var client = new DouyuDanmakuClient("  9999  ");
        Assert.Equal("9999", client.RoomId);
    }

    [Fact]
    public async Task Constructor_RejectsEmptyRoomId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(new DouyuDanmakuClient(" ")));
    }

    [Fact]
    public async Task Constructor_RejectsCookieWithNewline()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(new DouyuDanmakuClient("9999", "a=b\r\nc=d")));
    }

    [Fact]
    public async Task Constructor_RejectsNonDouyuLink()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(new DouyuDanmakuClient("https://live.bilibili.com/9999")));
    }

    [Fact]
    public async Task DisconnectAsync_WhenNeverConnected_IsSafe()
    {
        await using var client = new DouyuDanmakuClient("9999");
        await client.DisconnectAsync();
        await client.DisconnectAsync();
    }
}
