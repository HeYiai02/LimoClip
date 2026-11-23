using LiveMonitor.Core.Enums;
using LiveMonitor.Platforms;
using LiveMonitor.Plugins.YouTube;
using Xunit;

namespace LiveMonitor.Platforms.Tests.YouTube;

public sealed class YouTubeProviderTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    private static readonly string LiveJson = """
        {
          "id": "dQw4w9WgXcQ",
          "title": "Live Test Stream",
          "uploader": "Test Channel",
          "is_live": true,
          "formats": [
            { "format_id": "96", "height": 1080, "fps": 60, "tbr": 4500.5, "vcodec": "avc1.64002a", "url": "https://manifest.example/1080p60.m3u8", "http_headers": { "User-Agent": "yt-dlp-agent" } },
            { "format_id": "95", "height": 720, "fps": 30, "tbr": 2000.0, "vcodec": "avc1.4d401f", "url": "https://manifest.example/720p30.m3u8", "http_headers": {} },
            { "format_id": "94", "height": 720, "fps": 30, "tbr": 1500.0, "vcodec": "avc1.4d401f", "url": "https://manifest.example/720p30-low.m3u8", "http_headers": {} },
            { "format_id": "audio", "height": 0, "vcodec": "none", "url": "https://manifest.example/audio.m4a" }
          ]
        }
        """;

    [Fact]
    public void CanHandle_UsesUrlParser()
    {
        var provider = CreateProvider(_ => new YtDlpResult(0, LiveJson, string.Empty));

        Assert.True(provider.CanHandle($"https://www.youtube.com/watch?v={VideoId}"));
        Assert.False(provider.CanHandle("https://www.twitch.tv/channel"));
    }

    [Fact]
    public async Task GetRoomStatusAsync_Live_ReturnsQualitiesSortedAndDeduped()
    {
        var runner = new FakeRunner(_ => new YtDlpResult(0, LiveJson, string.Empty));
        var provider = new YouTubeProvider(runner);

        var status = await provider.GetRoomStatusAsync(VideoId);

        Assert.True(status.IsLive);
        Assert.Equal(PlatformType.YouTube, status.Platform);
        Assert.Equal("Live Test Stream", status.Title);
        Assert.Equal("Test Channel", status.AnchorName);
        Assert.Equal(2, status.AvailableQualities.Count);
        Assert.Equal(1080, status.AvailableQualities[0].Height);
        Assert.Equal(60, status.AvailableQualities[0].Fps);
        Assert.Equal("1080P60", status.AvailableQualities[0].DisplayName);
        Assert.Equal(720, status.AvailableQualities[1].Height);
        Assert.Equal("https://manifest.example/1080p60.m3u8", status.AvailableQualities[0].StreamUrl);

        var headers = status.AvailableQualities[0].RequiredHeaders;
        Assert.Equal("yt-dlp-agent", headers["User-Agent"]);
        Assert.Contains("youtube.com/watch", headers["Referer"], StringComparison.OrdinalIgnoreCase);

        // YouTube 直播 HLS 的音视频分离：纯音频 rendition 应被附加到每一路画质。
        Assert.True(status.AvailableQualities[0].HasSeparateAudio);
        Assert.All(
            status.AvailableQualities,
            quality => Assert.Equal("https://manifest.example/audio.m4a", quality.AudioStreamUrl));
        var audioHeaders = status.AvailableQualities[0].AudioHeaders;
        Assert.NotNull(audioHeaders);
        Assert.Contains("youtube.com/watch", audioHeaders!["Referer"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRoomStatusAsync_NotLive_ReturnsOffline()
    {
        const string offlineJson = """{ "id": "dQw4w9WgXcQ", "title": "Ended Stream", "uploader": "Channel", "is_live": false }""";
        var provider = CreateProvider(_ => new YtDlpResult(0, offlineJson, string.Empty));

        var status = await provider.GetRoomStatusAsync(VideoId);

        Assert.False(status.IsLive);
        Assert.Empty(status.AvailableQualities);
        Assert.Equal("Ended Stream", status.Title);
    }

    [Fact]
    public async Task GetRoomStatusAsync_YtDlpReportsNotLive_ReturnsOffline()
    {
        var provider = CreateProvider(_ => new YtDlpResult(
            1,
            string.Empty,
            "ERROR: [youtube] dQw4w9WgXcQ: This live event will begin in 2 hours."));

        var status = await provider.GetRoomStatusAsync(VideoId);

        Assert.False(status.IsLive);
    }

    [Fact]
    public async Task GetRoomStatusAsync_OtherYtDlpFailure_ThrowsWithStderr()
    {
        var provider = CreateProvider(_ => new YtDlpResult(2, string.Empty, "ERROR: network unreachable"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRoomStatusAsync(VideoId));

        Assert.Contains("exit 2", exception.Message);
        Assert.Contains("network unreachable", exception.Message);
    }

    [Fact]
    public async Task GetRoomStatusAsync_Handle_RequestsLivePageAndPassesCookie()
    {
        var runner = new FakeRunner(_ => new YtDlpResult(0, LiveJson, string.Empty));
        var provider = new YouTubeProvider(runner);

        await provider.GetRoomStatusAsync("https://www.youtube.com/@LofiGirl/live", "SID=abc; HSID=def");

        Assert.Equal("https://www.youtube.com/@LofiGirl/live", runner.LastUrl);
        Assert.Equal("SID=abc; HSID=def", runner.LastCookie);
    }

    [Fact]
    public async Task GetRoomStatusAsync_LiveWithoutFormats_Throws()
    {
        const string noFormats = """{ "id": "dQw4w9WgXcQ", "title": "Live", "uploader": "Channel", "is_live": true }""";
        var provider = CreateProvider(_ => new YtDlpResult(0, noFormats, string.Empty));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRoomStatusAsync(VideoId));

        Assert.Contains("可播放格式", exception.Message);
    }

    [Fact]
    public async Task GetRoomStatusAsync_MalformedJson_Throws()
    {
        var provider = CreateProvider(_ => new YtDlpResult(0, "{not-json", string.Empty));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRoomStatusAsync(VideoId));

        Assert.Contains("无法解析", exception.Message);
    }

    private static YouTubeProvider CreateProvider(Func<string, YtDlpResult> response)
        => new(new FakeRunner(response));

    private sealed class FakeRunner : IYtDlpRunner
    {
        private readonly Func<string, YtDlpResult> _response;

        public FakeRunner(Func<string, YtDlpResult> response)
        {
            _response = response;
        }

        public string? LastUrl { get; private set; }

        public string? LastCookie { get; private set; }

        public Task<YtDlpResult> RunJsonAsync(string url, string? cookie, CancellationToken ct = default)
        {
            LastUrl = url;
            LastCookie = cookie;
            return Task.FromResult(_response(url));
        }
    }
}
