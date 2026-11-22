using System.Globalization;
using System.Net;
using LiveMonitor.Core.Enums;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class BilibiliProviderTests
{
    private const string AnchorInfo = """
        {"code":0,"data":{"info":{"uname":"测试主播"}}}
        """;

    [Theory]
    [InlineData("https://live.bilibili.com/7734200?spm=1", "7734200")]
    [InlineData("https://live.bilibili.com/blanc/123", "123")]
    [InlineData("https://live.bilibili.com/h5/456", "456")]
    [InlineData("https://www.bilibili.com/blackboard/live/789.html", "789")]
    [InlineData("0006", "6")]
    public void ExtractRoomId_ParsesSupportedLinks(string input, string expected)
    {
        var provider = new BilibiliProvider(new HttpClient(new TestHttpMessageHandler(_ => throw new InvalidOperationException())));

        Assert.Equal(expected, provider.ExtractRoomId(input));
    }

    [Theory]
    [InlineData("https://live.bilibili.com/1", true)]
    [InlineData("https://b23.tv/abc", true)]
    [InlineData("7734200", true)]
    [InlineData("https://space.bilibili.com/123", false)]
    [InlineData("https://www.bilibili.com/video/BV1xx", false)]
    [InlineData("https://www.twitch.tv/eslcs", false)]
    public void CanHandle_RecognizesBilibiliOnly(string input, bool expected)
    {
        var provider = new BilibiliProvider(new HttpClient(new TestHttpMessageHandler(_ => throw new InvalidOperationException())));

        Assert.Equal(expected, provider.CanHandle(input));
    }

    [Fact]
    public async Task GetRoomStatus_Live_ParsesRoomAndCurrentQuality()
    {
        var handler = CreateHandler();
        var provider = new BilibiliProvider(new HttpClient(handler));

        var status = await provider.GetRoomStatusAsync("6", "SESSDATA=abc");

        Assert.True(status.IsLive);
        Assert.Equal("7734200", status.RoomId);
        Assert.Equal("测试直播间", status.Title);
        Assert.Equal("测试主播", status.AnchorName);

        var quality = Assert.Single(status.AvailableQualities);
        Assert.Equal("250", quality.Id);
        Assert.Equal("720P", quality.DisplayName);
        Assert.Equal(720, quality.Height);
        Assert.Equal(30, quality.Fps);
        Assert.Equal("https://cdn.example.com/live-bvc/test.flv?expires=1&qn=250", quality.StreamUrl);
        Assert.Equal("SESSDATA=abc", quality.RequiredHeaders["Cookie"]);
        Assert.Equal("https://live.bilibili.com/7734200", quality.RequiredHeaders["Referer"]);

        Assert.Contains(handler.Requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/getRoomPlayInfo", StringComparison.Ordinal)
            && request.RequestUri.Query.Contains("qn=10000", StringComparison.Ordinal));
        Assert.Equal("SESSDATA=abc", handler.Requests[0].Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task GetStream_WithRequestedQn_UsesThatQnAndReportsActualQn()
    {
        var handler = CreateHandler();
        var provider = new BilibiliProvider(new HttpClient(handler));

        var quality = await provider.GetStreamAsync("7734200", "150", "SESSDATA=abc");

        Assert.NotNull(quality);
        Assert.Equal("150", quality!.Id);
        Assert.Equal(480, quality.Height);
        Assert.Equal("https://cdn.example.com/live-bvc/test.flv?expires=1&qn=150", quality.StreamUrl);
        Assert.Contains("qn=150", handler.Requests.Single().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoomStatus_Offline_DoesNotRequestPlayInfo()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/room/v1/Room/get_info", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.Json(RoomInfo(liveStatus: 0));
            }

            if (path.EndsWith("/get_anchor_in_room", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.Json(AnchorInfo);
            }

            throw new InvalidOperationException("离线房间不应请求播放信息：" + request.RequestUri);
        });
        var provider = new BilibiliProvider(new HttpClient(handler));

        var status = await provider.GetRoomStatusAsync("7734200");

        Assert.False(status.IsLive);
        Assert.Empty(status.AvailableQualities);
        Assert.Equal("测试主播", status.AnchorName);
    }

    [Fact]
    public async Task GetRoomStatus_ApiError_ThrowsWithCode()
    {
        var handler = new TestHttpMessageHandler(_ =>
            TestHttpMessageHandler.Json("""{"code":19002000,"message":"房间不存在"}"""));
        var provider = new BilibiliProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRoomStatusAsync("1"));

        Assert.Contains("19002000", error.Message);
    }

    [Fact]
    public async Task GetRoomStatus_HttpError_ThrowsWithStatus()
    {
        var handler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("bad gateway")
            });
        var provider = new BilibiliProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetRoomStatusAsync("1"));

        Assert.Contains("502", error.Message);
    }

    [Fact]
    public async Task GetStream_RealFixture_ParsesFlvUrl()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "bilibili-playinfo.json"));
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(fixture));
        var provider = new BilibiliProvider(new HttpClient(handler));

        var quality = await provider.GetStreamAsync("7734200");

        Assert.NotNull(quality);
        Assert.Equal("250", quality!.Id);
        Assert.Equal("720P", quality.DisplayName);
        Assert.Equal(720, quality.Height);
        Assert.StartsWith("https://d1--cn-gotcha08.bilivideo.com/live-bvc/", quality.StreamUrl);
        Assert.Contains("qn=250", quality.StreamUrl, StringComparison.Ordinal);
    }

    private static TestHttpMessageHandler CreateHandler()
    {
        return new TestHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/room/v1/Room/get_info", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.Json(RoomInfo());
            }

            if (path.EndsWith("/get_anchor_in_room", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.Json(AnchorInfo);
            }

            if (path.EndsWith("/getRoomPlayInfo", StringComparison.Ordinal))
            {
                var requestedQn = ParseQn(request.RequestUri.Query);
                // 模拟游客态：请求 10000 时 B 站把 current_qn 压到 250。
                var actualQn = requestedQn == 10000 ? 250 : requestedQn;
                return TestHttpMessageHandler.Json(PlayInfo(actualQn));
            }

            throw new InvalidOperationException("未预期的 B 站请求：" + request.RequestUri);
        });
    }

    private static int ParseQn(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("qn=", StringComparison.Ordinal)
                && int.TryParse(pair[3..], out var qn))
            {
                return qn;
            }
        }

        return 250;
    }

    private static string RoomInfo(int liveStatus = 1)
        => """
            {"code":0,"message":"0","data":{"room_id":7734200,"short_id":6,"live_status":__LIVE__,"title":"测试直播间","uid":123}}
            """
            .Replace("__LIVE__", liveStatus.ToString(CultureInfo.InvariantCulture));

    private static string PlayInfo(int qn)
    {
        var description = qn switch
        {
            10000 => "原画",
            400 => "蓝光",
            250 => "720P",
            150 => "480P",
            80 => "360P",
            _ => $"{qn}P"
        };
        var detail = qn >= 10000 ? "高帧率" : string.Empty;

        return """
            {
              "code": 0,
              "data": {
                "playurl_info": {
                  "playurl": {
                    "g_qn_desc": [
                      {
                        "qn": __QN__,
                        "desc": "__DESC__",
                        "media_base_desc": {
                          "brief_desc": { "desc": "__DESC__" },
                          "detail_desc": { "desc": "__DETAIL__" }
                        }
                      }
                    ],
                    "stream": [
                      {
                        "protocol_name": "http_stream",
                        "format": [
                          {
                            "format_name": "flv",
                            "codec": [
                              {
                                "codec_name": "avc",
                                "current_qn": __QN__,
                                "accept_qn": [10000, 400, 250, 150],
                                "base_url": "/live-bvc/test.flv?",
                                "url_info": [
                                  {
                                    "host": "https://cdn.example.com",
                                    "extra": "expires=1&qn=__QN__"
                                  }
                                ]
                              }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """
            .Replace("__QN__", qn.ToString(CultureInfo.InvariantCulture))
            .Replace("__DESC__", description)
            .Replace("__DETAIL__", detail);
    }
}
