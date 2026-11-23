using System.Net.Http;
using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuProviderTests
{
    private static HttpClient CreateClient()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("homeH5Enc", StringComparison.Ordinal))
            {
                return DouyuTestData.JsonResponse(DouyuTestData.HomeH5EncJson());
            }

            if (path.Contains("betard", StringComparison.Ordinal))
            {
                return DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true));
            }

            return DouyuTestData.JsonResponse(DouyuTestData.StreamJson());
        });
        return DouyuTestData.CreateClient(handler);
    }

    [Fact]
    public void Platform_IsDouyu()
    {
        using var httpClient = CreateClient();
        var provider = new DouyuProvider(httpClient);
        Assert.Equal(PlatformType.Douyu, provider.Platform);
    }

    [Theory]
    [InlineData("https://www.douyu.com/9999", true)]
    [InlineData("https://www.douyu.com/9999?dyshid=abc", true)]
    [InlineData("9999", false)]
    [InlineData("https://live.bilibili.com/9999", false)]
    public void CanHandle_OnlyDouyuLinks(string url, bool expected)
    {
        using var httpClient = CreateClient();
        var provider = new DouyuProvider(httpClient);
        Assert.Equal(expected, provider.CanHandle(url));
    }

    [Fact]
    public async Task GetRoomStatusAsync_Live_ReturnsQualityWithSignedStream()
    {
        using var httpClient = CreateClient();
        var provider = new DouyuProvider(httpClient);

        var status = await provider.GetRoomStatusAsync("9999");

        Assert.True(status.IsLive);
        Assert.Equal(PlatformType.Douyu, status.Platform);
        Assert.Equal("9999", status.RoomId);
        Assert.Equal("陪伴每一天！", status.Title);
        Assert.Equal("yyfyyf", status.AnchorName);
        var quality = Assert.Single(status.AvailableQualities);
        Assert.StartsWith("https://stream.example/live/", quality.StreamUrl);
        Assert.Contains("Mozilla", quality.RequiredHeaders["User-Agent"]);
    }

    [Fact]
    public async Task GetRoomStatusAsync_Offline_ReturnsOfflineStatus()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("homeH5Enc", StringComparison.Ordinal))
            {
                return DouyuTestData.JsonResponse(DouyuTestData.HomeH5EncJson());
            }

            return DouyuTestData.JsonResponse(DouyuTestData.BetardJson(false));
        });
        using var httpClient = DouyuTestData.CreateClient(handler);
        var provider = new DouyuProvider(httpClient);

        var status = await provider.GetRoomStatusAsync("9999");

        Assert.False(status.IsLive);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public void ExtractRoomId_DelegatesToParser()
    {
        using var httpClient = CreateClient();
        var provider = new DouyuProvider(httpClient);
        Assert.Equal("12345", provider.ExtractRoomId("https://www.douyu.com/12345"));
        Assert.Throws<ArgumentException>(() => provider.ExtractRoomId("https://live.bilibili.com/1"));
    }
}
