using LiveMonitor.Plugins.Huya;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaApiClientTests
{
    [Fact]
    public async Task GetRoomAsync_ParsesLiveSnapshot()
    {
        var requests = new List<HttpRequestMessage>();
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(request =>
        {
            requests.Add(request);
            return HuyaTestData.Json(HuyaTestData.LiveJson);
        }));

        var snapshot = await api.GetRoomAsync("660000", CancellationToken.None);

        Assert.True(snapshot.IsLive);
        Assert.Equal("660000", snapshot.RoomId);
        Assert.Equal(1346609715L, snapshot.PresenterUid);
        Assert.Equal("虎牙赛事", snapshot.Nick);
        Assert.Equal("S16 赛事直播", snapshot.RoomName);
        Assert.Equal(2, snapshot.FlvLines.Count);
        Assert.Equal("https://al.flv.huya.com/src/660000-1.flv?wsSecret=abc&wsTime=1", snapshot.FlvLines[0].Url);
        Assert.Equal("AL", snapshot.FlvLines[0].CdnType);
        Assert.Equal(2, snapshot.BitRates.Count);
        Assert.Equal("蓝光10M", snapshot.BitRates[0].DisplayName);
        Assert.Equal(0, snapshot.BitRates[0].BitRate);
        Assert.Contains("roomid=660000", requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetRoomAsync_OfflineRoom_ReturnsNotLive()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.OfflineJson)));

        var snapshot = await api.GetRoomAsync("660000", CancellationToken.None);

        Assert.False(snapshot.IsLive);
        Assert.Equal("660000", snapshot.RoomId);
        Assert.Empty(snapshot.FlvLines);
    }

    [Fact]
    public async Task GetRoomAsync_StatusCode422_Throws()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.NotFoundJson)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.GetRoomAsync("999999999999", CancellationToken.None));
        Assert.Contains("422", error.Message);
    }

    [Fact]
    public async Task GetRoomAsync_EmptyRoomToken_Throws()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => api.GetRoomAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task GetRoomAsync_ResolvesHost_FromGzippedRoomPage()
    {
        var pageRequests = 0;
        var apiRequests = 0;
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Equals("/lpl", StringComparison.OrdinalIgnoreCase))
            {
                pageRequests++;
                return HuyaTestData.GzipHtml(HuyaTestData.RoomPageHtml);
            }

            apiRequests++;
            return HuyaTestData.Json(HuyaTestData.LiveJson);
        }));

        var snapshot = await api.GetRoomAsync("lpl", CancellationToken.None);
        Assert.Equal("660000", snapshot.RoomId);
        Assert.Equal(1, pageRequests);

        // 第二次应命中域名缓存，不再抓房间页。
        var second = await api.GetRoomAsync("lpl", CancellationToken.None);
        Assert.Equal("660000", second.RoomId);
        Assert.Equal(1, pageRequests);
        Assert.Equal(2, apiRequests);
    }

    [Fact]
    public async Task GetRoomAsync_HtmlWithoutProfileRoom_Throws()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(request =>
            request.RequestUri!.AbsolutePath.Equals("/nobody", StringComparison.OrdinalIgnoreCase)
                ? HuyaTestData.GzipHtml("<html><body>404</body></html>")
                : HuyaTestData.Json(HuyaTestData.LiveJson)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.GetRoomAsync("nobody", CancellationToken.None));
        Assert.Contains("数字房间号", error.Message);
    }
}
