using System.Net;
using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinApiClientTests
{
    [Fact]
    public async Task GetRoomAsync_LiveRoom_ReturnsQualities()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse("ttwid-abc")
                : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        var snapshot = await client.GetRoomAsync("141601416352");

        Assert.True(snapshot.IsLive);
        Assert.Equal("141601416352", snapshot.WebRid);
        Assert.Equal("7711351763655969576", snapshot.RoomId);
        Assert.Equal("看海老师", snapshot.Nick);
        Assert.Equal(4, snapshot.Qualities.Count);
        Assert.Equal("原画 1080P", snapshot.Qualities[0].DisplayName);
        Assert.Equal(1080, snapshot.Qualities[0].Height);
        Assert.Equal(PlatformType.Douyin, PlatformType.Douyin);

        var enterRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains("webcast/room/web/enter", StringComparison.Ordinal));
        Assert.Contains("web_rid=141601416352", enterRequest.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("ttwid=ttwid-abc", enterRequest.Headers.GetValues("Cookie").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRoomAsync_OfflineRoom_ReturnsNoQualities()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.OfflineJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        var snapshot = await client.GetRoomAsync("7555682083511913256");

        Assert.False(snapshot.IsLive);
        Assert.Empty(snapshot.Qualities);
        Assert.Equal("某主播", snapshot.Nick);
    }

    [Fact]
    public async Task GetRoomAsync_RiskControl_ThrowsChineseError()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.RiskJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRoomAsync("141601416352"));
        Assert.Contains("风控", exception.Message);
        Assert.Contains("10011", exception.Message);
    }

    [Fact]
    public async Task GetRoomAsync_EmptyRooms_Throws()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.EmptyRoomJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRoomAsync("141601416352"));
        Assert.Contains("不存在或已关闭", exception.Message);
    }

    [Fact]
    public async Task GetTtwidAsync_NoCookie_Throws()
    {
        var handler = new DouyinTestData.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>"),
        });
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTtwidAsync());
        Assert.Contains("ttwid", exception.Message);
    }

    [Fact]
    public async Task GetTtwidAsync_CachesValue()
    {
        var handler = new DouyinTestData.StubHandler(_ => DouyinTestData.HomepageResponse("cached-ttwid"));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var client = new DouyinApiClient(http);

        Assert.Equal("cached-ttwid", await client.GetTtwidAsync());
        Assert.Equal("cached-ttwid", await client.GetTtwidAsync());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ParseTtwid_ExtractsCookieValue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "foo=1; Path=/");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "ttwid=1%7Cabc%7C123; Path=/; HttpOnly");
        Assert.Equal("1%7Cabc%7C123", DouyinApiClient.ParseTtwid(response));
    }

    [Fact]
    public void ParseQualities_PrefersFlvThenHls()
    {
        var room = System.Text.Json.JsonDocument.Parse(DouyinTestData.LiveJson)
            .RootElement.GetProperty("data").GetProperty("data")[0];

        var qualities = DouyinApiClient.ParseQualities(room, "141601416352");

        Assert.Equal(4, qualities.Count);
        Assert.All(qualities, q => Assert.Contains("pull-flv.example", q.StreamUrl, StringComparison.Ordinal));
        Assert.Equal("https://live.douyin.com/141601416352", qualities[0].RequiredHeaders["Referer"]);
    }
}
