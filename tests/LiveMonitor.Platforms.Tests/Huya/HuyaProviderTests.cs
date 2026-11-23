using LiveMonitor.Plugins.Huya;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaProviderTests
{
    [Fact]
    public void Platform_IsHuya()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        Assert.Equal(PlatformType.Huya, provider.Platform);
    }

    [Theory]
    [InlineData("https://www.huya.com/660000", true)]
    [InlineData("660000", false)]
    [InlineData("https://live.bilibili.com/1", false)]
    public void CanHandle_MatchesHuyaLinks(string url, bool expected)
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        Assert.Equal(expected, provider.CanHandle(url));
    }

    [Fact]
    public async Task GetRoomStatusAsync_Live_ReturnsQualityLadderOnStableLine()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync("https://www.huya.com/660000", ct: CancellationToken.None);

        Assert.True(status.IsLive);
        Assert.Equal("660000", status.RoomId);
        Assert.Equal(PlatformType.Huya, status.Platform);
        Assert.Equal("S16 赛事直播", status.Title);
        Assert.Equal("虎牙赛事", status.AnchorName);
        Assert.Equal(2, status.AvailableQualities.Count);

        // 最高档在前：蓝光20M（iBitRate=0 → 房间 bitRate=20000），线路优先 HS。
        var quality = status.AvailableQualities[0];
        Assert.Equal("蓝光20M", quality.DisplayName);
        Assert.Equal("huya-flv-20000", quality.Id);
        Assert.Equal(1080, quality.Height);
        Assert.StartsWith("https://hs.flv.huya.com/", quality.StreamUrl);
        Assert.Contains("&ratio=20000", quality.StreamUrl);
        Assert.Equal("https://www.huya.com/660000", quality.RequiredHeaders["Referer"]);
        Assert.False(string.IsNullOrWhiteSpace(quality.RequiredHeaders["User-Agent"]));
        Assert.False(quality.RequiredHeaders.ContainsKey("Cookie"));
        Assert.False(quality.HasSeparateAudio);

        var hd = status.AvailableQualities[1];
        Assert.Equal("蓝光4M", hd.DisplayName);
        Assert.Equal("huya-flv-4000", hd.Id);
        Assert.Contains("&ratio=4000", hd.StreamUrl);
    }

    [Fact]
    public async Task GetRoomStatusAsync_WithCookie_PassesCookieToApiAndHeaders()
    {
        var requests = new List<HttpRequestMessage>();
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(request =>
        {
            requests.Add(request);
            return HuyaTestData.Json(HuyaTestData.LiveJson);
        }));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync(
            "660000",
            "yyuid=123; udb_passport=abc",
            CancellationToken.None);

        Assert.Single(requests);
        Assert.True(requests[0].Headers.TryGetValues("Cookie", out var values));
        Assert.Contains(values, value => value.Contains("yyuid=123", StringComparison.Ordinal));

        var quality = status.AvailableQualities[0];
        Assert.Equal("yyuid=123; udb_passport=abc", quality.RequiredHeaders["Cookie"]);
    }

    [Fact]
    public async Task GetRoomStatusAsync_NumericRoomId_Works()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync("660000", ct: CancellationToken.None);

        Assert.True(status.IsLive);
        Assert.Equal(2, status.AvailableQualities.Count);
    }

    [Fact]
    public async Task GetRoomStatusAsync_Offline_ReturnsOfflineStatus()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.OfflineJson)));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync("https://www.huya.com/660000", ct: CancellationToken.None);

        Assert.False(status.IsLive);
        Assert.Equal("660000", status.RoomId);
        Assert.Equal("S16 赛事直播", status.Title);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public void ExtractRoomId_NonHuyaLink_Throws()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        Assert.Throws<ArgumentException>(() => provider.ExtractRoomId("https://live.bilibili.com/1"));
    }
}
