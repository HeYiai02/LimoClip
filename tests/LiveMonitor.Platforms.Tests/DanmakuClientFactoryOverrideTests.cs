using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class DanmakuClientFactoryOverrideTests
{
    [Fact]
    public void Create_UnsupportedPlatformWithOverride_ReturnsOverrideClient()
    {
        var factory = new DanmakuClientFactory();

        factory.RegisterOrReplace(PlatformType.YouTube, (roomId, _, _) => new FakeDanmakuClient(PlatformType.YouTube, roomId));

        Assert.True(factory.HasOverride(PlatformType.YouTube));
        var client = factory.Create(PlatformType.YouTube, "abc", cookie: null);
        var fake = Assert.IsType<FakeDanmakuClient>(client);
        Assert.Equal(PlatformType.YouTube, fake.Platform);
        Assert.Equal("abc", fake.RoomId);
    }

    [Fact]
    public void RegisterOrReplace_Twice_LastFactoryWins()
    {
        var factory = new DanmakuClientFactory();
        factory.RegisterOrReplace(PlatformType.Douyu, (roomId, _, _) => new FakeDanmakuClient(PlatformType.Douyu, "first"));
        factory.RegisterOrReplace(PlatformType.Douyu, (roomId, _, _) => new FakeDanmakuClient(PlatformType.Douyu, roomId));

        var client = Assert.IsType<FakeDanmakuClient>(factory.Create(PlatformType.Douyu, "second", cookie: null));

        Assert.Equal("second", client.RoomId);
    }

    [Fact]
    public void Override_ReceivesRoomIdCookieAndHttpClient()
    {
        var factory = new DanmakuClientFactory();
        string? seenRoomId = null;
        string? seenCookie = null;
        HttpClient? seenHttpClient = null;
        factory.RegisterOrReplace(PlatformType.Huya, (roomId, cookie, httpClient) =>
        {
            seenRoomId = roomId;
            seenCookie = cookie;
            seenHttpClient = httpClient;
            return new FakeDanmakuClient(PlatformType.Huya, roomId);
        });

        factory.Create(PlatformType.Huya, "520520", "cookie-value");

        Assert.Equal("520520", seenRoomId);
        Assert.Equal("cookie-value", seenCookie);
        Assert.NotNull(seenHttpClient);
    }

    [Fact]
    public void Remove_Override_FallsBackToBuiltInAndNullForUnsupported()
    {
        var factory = new DanmakuClientFactory();
        factory.RegisterOrReplace(PlatformType.Douyin, (roomId, _, _) => new FakeDanmakuClient(PlatformType.Douyin, roomId));

        Assert.True(factory.Remove(PlatformType.Douyin));
        Assert.False(factory.Remove(PlatformType.Douyin));
        Assert.False(factory.HasOverride(PlatformType.Douyin));
        Assert.Null(factory.Create(PlatformType.Douyin, "123", cookie: null));

        Assert.IsType<BilibiliDanmakuClient>(factory.Create(PlatformType.Bilibili, "7734200", cookie: null));
        Assert.IsType<TwitchDanmakuClient>(factory.Create(PlatformType.Twitch, "somechannel", cookie: null));
    }

    [Fact]
    public void Create_NullRoomId_ThrowsEvenWithOverride()
    {
        var factory = new DanmakuClientFactory();
        factory.RegisterOrReplace(PlatformType.YouTube, (roomId, _, _) => new FakeDanmakuClient(PlatformType.YouTube, roomId));

        Assert.Throws<ArgumentNullException>(() => factory.Create(PlatformType.YouTube, null!, cookie: null));
    }

    private sealed class FakeDanmakuClient : IDanmakuClient
    {
        public FakeDanmakuClient(PlatformType platform, string roomId)
        {
            Platform = platform;
            RoomId = roomId;
        }

        public PlatformType Platform { get; }

        public string RoomId { get; }

        // 测试用假客户端不触发事件；自定义访问器避免 CS0067 警告。
        public event Action<DanmakuMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public event Action<Exception?>? Disconnected
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
