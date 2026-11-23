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
    public async Task GetRoomStatusAsync_Live_ReturnsSingleQualityWithHeaders()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync("https://www.huya.com/660000", ct: CancellationToken.None);

        Assert.True(status.IsLive);
        Assert.Equal("660000", status.RoomId);
        Assert.Equal(PlatformType.Huya, status.Platform);
        Assert.Equal("S16 赛事直播", status.Title);
        Assert.Equal("虎牙赛事", status.AnchorName);
        var quality = Assert.Single(status.AvailableQualities);
        Assert.Equal("蓝光10M", quality.DisplayName);
        Assert.StartsWith("https://al.flv.huya.com/", quality.StreamUrl);
        Assert.Equal("https://www.huya.com/660000", quality.RequiredHeaders["Referer"]);
        Assert.False(string.IsNullOrWhiteSpace(quality.RequiredHeaders["User-Agent"]));
        Assert.False(quality.HasSeparateAudio);
    }

    [Fact]
    public async Task GetRoomStatusAsync_NumericRoomId_Works()
    {
        using var api = new HuyaApiClient(HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson)));
        var provider = new HuyaProvider(api);

        var status = await provider.GetRoomStatusAsync("660000", ct: CancellationToken.None);

        Assert.True(status.IsLive);
        Assert.Single(status.AvailableQualities);
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
