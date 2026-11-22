using LiveMonitor.Core.Enums;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

internal sealed class FakeYtDlpRunner : IYtDlpRunner
{
    public YtDlpResult Result { get; set; } = new(0, "{}", string.Empty);

    public string? LastUrl { get; private set; }

    public string? LastCookie { get; private set; }

    public Task<YtDlpResult> RunJsonAsync(string url, string? cookie, CancellationToken ct = default)
    {
        LastUrl = url;
        LastCookie = cookie;
        return Task.FromResult(Result);
    }
}

public sealed class TwitchProviderTests
{
    private const string LiveJson = """
        {
          "id": "123456789",
          "title": "ESLCS 直播",
          "uploader": "ESLCS",
          "channel": "eslcs",
          "is_live": true,
          "live_status": "is_live",
          "formats": [
            {
              "format_id": "1080p60",
              "height": 1080,
              "fps": 60,
              "vcodec": "avc1.64002a",
              "protocol": "m3u8_native",
              "url": "https://example.com/1080p60.m3u8",
              "tbr": 6000.0,
              "http_headers": {
                "User-Agent": "UA-1",
                "Referer": "https://www.twitch.tv/"
              }
            },
            {
              "format_id": "720p",
              "height": 720,
              "fps": 30,
              "vcodec": "avc1.4d401f",
              "protocol": "m3u8_native",
              "url": "https://example.com/720p.m3u8",
              "tbr": 3000.0
            },
            {
              "format_id": "720p-low",
              "height": 720,
              "fps": 30,
              "vcodec": "avc1.4d401f",
              "protocol": "m3u8_native",
              "url": "https://example.com/720p-low.m3u8",
              "tbr": 1000.0
            },
            {
              "format_id": "audio",
              "height": null,
              "fps": null,
              "vcodec": "none",
              "acodec": "mp4a.40.2",
              "url": "https://example.com/audio.m3u8",
              "tbr": 160.0
            }
          ]
        }
        """;

    [Fact]
    public async Task GetRoomStatus_Live_ParsesFormatsAndPassesCookie()
    {
        var runner = new FakeYtDlpRunner
        {
            Result = new YtDlpResult(0, LiveJson, string.Empty)
        };
        var provider = new TwitchProvider(runner);

        var status = await provider.GetRoomStatusAsync("https://www.twitch.tv/ESLCS", "auth-token=abc");

        Assert.True(status.IsLive);
        Assert.Equal(PlatformType.Twitch, status.Platform);
        Assert.Equal("eslcs", status.RoomId);
        Assert.Equal("ESLCS 直播", status.Title);
        Assert.Equal("ESLCS", status.AnchorName);
        Assert.Equal("https://www.twitch.tv/eslcs", runner.LastUrl);
        Assert.Equal("auth-token=abc", runner.LastCookie);

        Assert.Equal(2, status.AvailableQualities.Count);
        var best = status.AvailableQualities[0];
        Assert.Equal("twitch-1080p60", best.Id);
        Assert.Equal("1080P60", best.DisplayName);
        Assert.Equal(1080, best.Height);
        Assert.Equal(60, best.Fps);
        Assert.Equal("https://example.com/1080p60.m3u8", best.StreamUrl);
        Assert.Equal("UA-1", best.RequiredHeaders["User-Agent"]);
        Assert.Equal("https://www.twitch.tv/", best.RequiredHeaders["Referer"]);

        var second = status.AvailableQualities[1];
        Assert.Equal("twitch-720p30", second.Id);
        Assert.Equal("720P", second.DisplayName);
        Assert.Equal("https://example.com/720p.m3u8", second.StreamUrl);
        Assert.True(second.RequiredHeaders.ContainsKey("User-Agent"));
        Assert.Equal("https://www.twitch.tv/eslcs", second.RequiredHeaders["Referer"]);
    }

    [Fact]
    public async Task GetRoomStatus_Offline_ReturnsOfflineInsteadOfThrowing()
    {
        var runner = new FakeYtDlpRunner
        {
            Result = new YtDlpResult(
                1,
                string.Empty,
                "ERROR: [twitch:stream] shroud: The channel is not currently live")
        };
        var provider = new TwitchProvider(runner);

        var status = await provider.GetRoomStatusAsync("shroud");

        Assert.False(status.IsLive);
        Assert.Equal("shroud", status.RoomId);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public async Task GetRoomStatus_NonLiveJson_ReturnsOffline()
    {
        var runner = new FakeYtDlpRunner
        {
            Result = new YtDlpResult(
                0,
                """{"is_live":false,"title":"离线标题","uploader":"主播","channel":"foo","formats":[]}""",
                string.Empty)
        };
        var provider = new TwitchProvider(runner);

        var status = await provider.GetRoomStatusAsync("foo");

        Assert.False(status.IsLive);
        Assert.Equal("离线标题", status.Title);
        Assert.Equal("主播", status.AnchorName);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public async Task GetRoomStatus_YtDlpError_ThrowsWithStderr()
    {
        var runner = new FakeYtDlpRunner
        {
            Result = new YtDlpResult(1, string.Empty, "ERROR: Unable to download webpage")
        };
        var provider = new TwitchProvider(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRoomStatusAsync("foo"));

        Assert.Contains("Unable to download", error.Message);
    }

    [Fact]
    public async Task GetRoomStatus_InvalidJson_Throws()
    {
        var runner = new FakeYtDlpRunner
        {
            Result = new YtDlpResult(0, "not json", string.Empty)
        };
        var provider = new TwitchProvider(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRoomStatusAsync("foo"));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/ESLCS?foo=1", "eslcs")]
    [InlineData("https://m.twitch.tv/ESLCS", "eslcs")]
    [InlineData("ESLCS", "eslcs")]
    public void ExtractRoomId_ParsesSupportedLinks(string input, string expected)
    {
        var provider = new TwitchProvider(new FakeYtDlpRunner());

        Assert.Equal(expected, provider.ExtractRoomId(input));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/eslcs/videos")]
    [InlineData("https://www.twitch.tv/videos/123")]
    [InlineData("https://clips.twitch.tv/abc")]
    [InlineData("https://www.twitch.tv/")]
    public void ExtractRoomId_RejectsNonChannelLinks(string input)
    {
        var provider = new TwitchProvider(new FakeYtDlpRunner());

        Assert.Throws<ArgumentException>(() => provider.ExtractRoomId(input));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/eslcs", true)]
    [InlineData("https://m.twitch.tv/eslcs", true)]
    [InlineData("eslcs", true)]
    [InlineData("https://www.twitch.tv/videos/123", false)]
    [InlineData("https://clips.twitch.tv/abc", false)]
    [InlineData("https://live.bilibili.com/1", false)]
    public void CanHandle_RecognizesTwitchChannels(string input, bool expected)
    {
        var provider = new TwitchProvider(new FakeYtDlpRunner());

        Assert.Equal(expected, provider.CanHandle(input));
    }
}
