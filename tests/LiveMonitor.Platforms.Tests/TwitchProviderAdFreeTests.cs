using System.Net;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>V0.5-G Twitch 去广告方案 B：外部代理 → 原生 GQL + Usher → yt-dlp 三层降级测试。</summary>
public sealed class TwitchProviderAdFreeTests
{
    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-TWITCH-INFO:NODE="abc",MANIFEST-NODE-TYPE="weaver"
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,FRAME-RATE=60.000,CODECS="avc1.64002a,mp4a.40.2"
        https://video-weaver.example.com/v1/playlist/1080p60.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,FRAME-RATE=60.000,CODECS="avc1.640020,mp4a.40.2"
        https://video-weaver.example.com/v1/playlist/720p60-high.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,FRAME-RATE=60.000,CODECS="avc1.640020,mp4a.40.2"
        https://video-weaver.example.com/v1/playlist/720p60-low.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720,FRAME-RATE=30.000,CODECS="avc1.640020,mp4a.40.2"
        https://video-weaver.example.com/v1/playlist/720p30.m3u8
        """;

    private const string AccessTokenJson = """
        [{"data":{"streamPlaybackAccessToken":{"value":"token-value","signature":"singature-value"}}}]
        """;

    private const string ChannelMetaJson = """
        {"data":{"user":{"displayName":"ESLCS","stream":{"title":"ESL 直播"}}}}
        """;

    [Fact]
    public async Task GetRoomStatus_WithProxyTemplate_ParsesPlaylistWithoutYtDlp()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.Contains("luminous.dev", request.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/live/eslcs", request.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            return Playlist(MasterPlaylist);
        });
        var runner = CreateRunner();
        var provider = new TwitchProvider(runner, CreateClient(handler), () => "https://eu.luminous.dev/live/{channel}");

        var status = await provider.GetRoomStatusAsync("eslcs");

        Assert.True(status.IsLive);
        Assert.Equal(3, status.AvailableQualities.Count);
        var best = status.AvailableQualities[0];
        Assert.Equal("twitch-1080p60", best.Id);
        Assert.Equal(1080, best.Height);
        Assert.Equal(60, best.Fps);
        Assert.Contains("1080p60.m3u8", best.StreamUrl, StringComparison.Ordinal);
        Assert.Equal("https://www.twitch.tv/eslcs", best.RequiredHeaders["Referer"]);

        // (height,fps) 去重保留 BANDWIDTH 最大的一条。
        var duplicated = Assert.Single(status.AvailableQualities, quality => quality.Id == "twitch-720p60");
        Assert.Contains("720p60-high", duplicated.StreamUrl, StringComparison.Ordinal);
        Assert.Equal("twitch-720p30", status.AvailableQualities[2].Id);

        // 代理成功时不应回退 yt-dlp。
        Assert.Null(runner.LastUrl);
    }

    [Fact]
    public async Task GetRoomStatus_WhenProxyFails_FallsBackToNativeGqlAndUsher()
    {
        var requests = new List<string>();
        var handler = new TestHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add(uri);
            if (uri.Contains("luminous.dev", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("kimne78kx3ncx6brgo4mv6wki5h1ko", HeaderValue(request, "Client-Id"));
                if (request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("PlaybackAccessToken", StringComparison.Ordinal))
                {
                    return Json(AccessTokenJson);
                }

                return Json(ChannelMetaJson);
            }

            return Playlist(MasterPlaylist);
        });
        var runner = CreateRunner();
        var provider = new TwitchProvider(runner, CreateClient(handler), () => "https://eu.luminous.dev/live/{channel}");

        var status = await provider.GetRoomStatusAsync("eslcs");

        Assert.True(status.IsLive);
        Assert.Equal(3, status.AvailableQualities.Count);
        Assert.Null(runner.LastUrl);
        Assert.Contains(requests, uri => uri.Contains("usher.ttvnw.net", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetRoomStatus_WithAuthCookie_UsesEmbedPlayerTypeAndAuthorizationHeader()
    {
        string? tokenAuthorization = null;
        string? tokenRequestBody = null;
        string? usherUrl = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("PlaybackAccessToken", StringComparison.Ordinal))
                {
                    tokenAuthorization = HeaderValue(request, "Authorization");
                    tokenRequestBody = body;
                    return Json(AccessTokenJson);
                }

                return Json(ChannelMetaJson);
            }

            usherUrl = request.RequestUri!.ToString();
            return Playlist(MasterPlaylist);
        });
        var provider = new TwitchProvider(CreateRunner(), CreateClient(handler));

        var status = await provider.GetRoomStatusAsync("eslcs", "auth-token=abc123; foo=bar");

        Assert.True(status.IsLive);
        Assert.Equal("ESL 直播", status.Title);
        Assert.Equal("ESLCS", status.AnchorName);
        Assert.Equal("OAuth abc123", tokenAuthorization);
        // 实测 site/popout/mobile_web 会拼接 SSAI 广告，embed 不拼，因此固定使用 embed。
        Assert.Contains("\"playerType\":\"embed\"", tokenRequestBody, StringComparison.Ordinal);
        Assert.NotNull(usherUrl);
        Assert.Contains("usher.ttvnw.net", usherUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allow_source=true", usherUrl, StringComparison.Ordinal);
        Assert.Contains("sig=singature-value", usherUrl, StringComparison.Ordinal);
        Assert.Contains("token=token-value", usherUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoomStatus_WhenNativeUsherFails_FallsBackToYtDlp()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(AccessTokenJson);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var runner = CreateRunner(exitCode: 0, stdout: LiveJson);
        var provider = new TwitchProvider(runner, CreateClient(handler));

        var status = await provider.GetRoomStatusAsync("eslcs");

        Assert.True(status.IsLive);
        Assert.Equal(2, status.AvailableQualities.Count);
        Assert.Equal("https://www.twitch.tv/eslcs", runner.LastUrl);
    }

    [Fact]
    public async Task GetRoomStatus_UsherNotFound_ReturnsOfflineWithoutYtDlp()
    {
        var handler = new TestHttpMessageHandler(request =>
            request.Method == HttpMethod.Post
                ? Json(AccessTokenJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var runner = CreateRunner();
        var provider = new TwitchProvider(runner, CreateClient(handler));

        var status = await provider.GetRoomStatusAsync("eslcs", cookie: null);

        Assert.False(status.IsLive);
        Assert.Null(runner.LastUrl);
    }

    private const string LiveJson = """
        {
          "title": "ESLCS 直播",
          "uploader": "ESLCS",
          "is_live": true,
          "formats": [
            { "height": 1080, "fps": 60, "vcodec": "avc1.64002a", "url": "https://example.com/1080p60.m3u8", "tbr": 6000.0 },
            { "height": 720, "fps": 30, "vcodec": "avc1.4d401f", "url": "https://example.com/720p30.m3u8", "tbr": 3000.0 }
          ]
        }
        """;

    private static HttpClient CreateClient(TestHttpMessageHandler handler)
        => new(handler) { Timeout = TimeSpan.FromSeconds(10) };

    private static FakeYtDlpRunner CreateRunner(int exitCode = 0, string stdout = "{}")
        => new() { Result = new YtDlpResult(exitCode, stdout, string.Empty) };

    private static HttpResponseMessage Playlist(string playlist)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(playlist, Encoding.UTF8, "application/vnd.apple.mpegurl")
        };

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string? HeaderValue(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
