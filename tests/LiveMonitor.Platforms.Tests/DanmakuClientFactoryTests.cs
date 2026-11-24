using LiveMonitor.Core.Enums;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class DanmakuClientFactoryTests
{
    [Fact]
    public void Create_Bilibili_ReturnsBilibiliClient()
    {
        var factory = new DanmakuClientFactory();

        var client = factory.Create(PlatformType.Bilibili, "7734200", cookie: null);

        var bilibili = Assert.IsType<BilibiliDanmakuClient>(client);
        Assert.Equal(PlatformType.Bilibili, bilibili.Platform);
        Assert.Equal("7734200", bilibili.RoomId);
    }

    [Fact]
    public void Create_Twitch_ReturnsTwitchClient()
    {
        var factory = new DanmakuClientFactory();

        var client = factory.Create(PlatformType.Twitch, "somestreamer", cookie: null);

        var twitch = Assert.IsType<TwitchDanmakuClient>(client);
        Assert.Equal(PlatformType.Twitch, twitch.Platform);
        Assert.Equal("somestreamer", twitch.RoomId);
    }

    [Fact]
    public void Create_CustomStream_ReturnsNullClient()
    {
        var factory = new DanmakuClientFactory();

        var client = factory.Create(PlatformType.CustomStream, "cs_abcd1234", cookie: null);

        var nullClient = Assert.IsType<LiveMonitor.Platforms.CustomStream.NullDanmakuClient>(client);
        Assert.Equal(PlatformType.CustomStream, nullClient.Platform);
        Assert.Equal("cs_abcd1234", nullClient.RoomId);
    }

    [Theory]
    [InlineData(PlatformType.Douyin)]
    [InlineData(PlatformType.Douyu)]
    [InlineData(PlatformType.Huya)]
    [InlineData(PlatformType.YouTube)]
    public void Create_UnsupportedPlatform_ReturnsNull(PlatformType platform)
    {
        var factory = new DanmakuClientFactory();

        Assert.Null(factory.Create(platform, "12345", cookie: null));
    }

    [Fact]
    public void Create_NullRoomId_Throws()
    {
        var factory = new DanmakuClientFactory();

        Assert.Throws<ArgumentNullException>(() => factory.Create(PlatformType.Bilibili, null!, cookie: null));
    }
}
