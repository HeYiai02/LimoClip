using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms.CustomStream;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class CustomStreamProviderTests
{
    [Theory]
    [InlineData("https://cdn.example.com/live/stream.m3u8")]
    [InlineData("https://cdn.example.com/live/stream.flv?token=abc")]
    [InlineData("https://cdn.example.com/live/index.ts")]
    [InlineData("https://cdn.example.com/live/manifest.mpd")]
    [InlineData("rtmp://live.example.com/live/stream")]
    [InlineData("rtsp://192.168.1.10/stream")]
    [InlineData("SRT://live.example.com:9000?mode=caller")]
    public void LooksLikeStreamUrl_RecognizesStreams(string url)
    {
        Assert.True(CustomStreamProvider.LooksLikeStreamUrl(url));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/123")]
    [InlineData("https://example.com/index.html")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeStreamUrl_RejectsNonStreams(string? url)
    {
        Assert.False(CustomStreamProvider.LooksLikeStreamUrl(url));
    }

    [Theory]
    [InlineData("https://cdn.example.com/live/token-abc", true)]
    [InlineData("http://cdn.example.com/a/no-extension", true)]
    [InlineData("rtsp://host/stream", true)]
    [InlineData("ftp://host/file", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    public void IsSupportedDirectUrl_AcceptsAnySupportedScheme(string url, bool expected)
    {
        Assert.Equal(expected, CustomStreamProvider.IsSupportedDirectUrl(url));
    }

    [Fact]
    public void ExtractRoomId_UsesCsPrefix_AndIsUnique()
    {
        var provider = new CustomStreamProvider();

        var first = provider.ExtractRoomId("https://cdn.example.com/live/a.m3u8");
        var second = provider.ExtractRoomId("https://cdn.example.com/live/a.m3u8");

        Assert.StartsWith("cs_", first, StringComparison.Ordinal);
        Assert.Equal(11, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task GetRoomStatusAsync_WithDirectUrl_ReturnsSinglePositiveQuality()
    {
        var provider = new CustomStreamProvider();
        const string url = "https://cdn.example.com/live/stream.m3u8";

        var status = await provider.GetRoomStatusAsync(url);

        Assert.True(status.IsLive);
        Assert.Equal(PlatformType.CustomStream, status.Platform);
        var quality = Assert.Single(status.AvailableQualities);
        Assert.Equal("direct", quality.Id);
        Assert.Equal(url, quality.StreamUrl);
        Assert.Empty(quality.RequiredHeaders);
    }

    [Fact]
    public async Task GetRoomStatusAsync_FromRepository_AppliesHeadersAndCookie()
    {
        var rooms = new FakeRoomRepository();
        var room = new LiveRoom
        {
            Id = Guid.NewGuid(),
            DisplayNumber = 1,
            Platform = PlatformType.CustomStream,
            RoomId = "cs_abcd1234",
            RawUrl = "https://cdn.example.com/live/stream.flv",
            Alias = "赛事清流",
            CustomHeadersJson = """{"Referer":"https://live.example.com/","X-Test":"1"}""",
            CreatedAt = DateTime.UtcNow,
        };
        rooms.Rooms.Add(room);
        var provider = new CustomStreamProvider(rooms);

        var status = await provider.GetRoomStatusAsync(room.RoomId, cookie: "SESSDATA=abc");

        Assert.Equal("赛事清流", status.Title);
        var quality = Assert.Single(status.AvailableQualities);
        Assert.Equal("https://live.example.com/", quality.RequiredHeaders["Referer"]);
        Assert.Equal("1", quality.RequiredHeaders["X-Test"]);
        Assert.Equal("SESSDATA=abc", quality.RequiredHeaders["Cookie"]);
    }

    [Fact]
    public async Task GetRoomStatusAsync_WithRtspUrl_DropsHttpHeaders()
    {
        var rooms = new FakeRoomRepository();
        var room = new LiveRoom
        {
            Id = Guid.NewGuid(),
            DisplayNumber = 1,
            Platform = PlatformType.CustomStream,
            RoomId = "cs_rtsp0001",
            RawUrl = "rtsp://192.168.1.10/live",
            Alias = "内网 RTSP",
            CustomHeadersJson = """{"Referer":"https://live.example.com/"}""",
            CreatedAt = DateTime.UtcNow,
        };
        rooms.Rooms.Add(room);
        var provider = new CustomStreamProvider(rooms);

        var status = await provider.GetRoomStatusAsync(room.RoomId, cookie: "cookie=1");

        var quality = Assert.Single(status.AvailableQualities);
        Assert.Empty(quality.RequiredHeaders);
    }

    [Fact]
    public async Task GetRoomStatusAsync_UnknownRoomId_Throws()
    {
        var provider = new CustomStreamProvider(new FakeRoomRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRoomStatusAsync("cs_missing"));
    }

    [Fact]
    public void ParseHeadersJson_InvalidJson_ReturnsEmpty()
    {
        Assert.Empty(CustomStreamProvider.ParseHeadersJson("{not-json"));
        Assert.Empty(CustomStreamProvider.ParseHeadersJson(null));
        Assert.Equal("v", CustomStreamProvider.ParseHeadersJson("""{"k":"v"}""")["k"]);
    }

    [Fact]
    public void BuildHeadersJson_Empty_ReturnsNull_AndIncludesNonEmptyValues()
    {
        Assert.Null(CustomStreamProvider.BuildHeadersJson(null, "  "));

        var json = CustomStreamProvider.BuildHeadersJson("https://live.example.com/", "UA-1");
        var headers = CustomStreamProvider.ParseHeadersJson(json);

        Assert.Equal("https://live.example.com/", headers["Referer"]);
        Assert.Equal("UA-1", headers["User-Agent"]);
    }

    private sealed class FakeRoomRepository : IRoomRepository
    {
        public List<LiveRoom> Rooms { get; } = new();

        public Task<IReadOnlyList<LiveRoom>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiveRoom>>(Rooms.ToList());

        public Task<LiveRoom?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Rooms.FirstOrDefault(room => room.Id == id));

        public Task<bool> ExistsAsync(PlatformType platform, string roomId, CancellationToken ct = default)
            => Task.FromResult(Rooms.Any(room => room.Platform == platform && room.RoomId == roomId));

        public Task<int> GetNextDisplayNumberAsync(CancellationToken ct = default)
            => Task.FromResult(Rooms.Count + 1);

        public Task AddAsync(LiveRoom room, CancellationToken ct = default)
        {
            Rooms.Add(room);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LiveRoom room, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Rooms.RemoveAll(room => room.Id == id);
            return Task.CompletedTask;
        }
    }
}
