using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinProviderTests
{
    [Fact]
    public void Platform_IsDouyin()
    {
        using var http = DouyinTestData.CreateHttpClient(new DouyinTestData.StubHandler(_ => DouyinTestData.HomepageResponse()));
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);
        Assert.Equal(PlatformType.Douyin, provider.Platform);
    }

    [Theory]
    [InlineData("https://live.douyin.com/141601416352", true)]
    [InlineData("141601416352", false)]
    [InlineData("https://live.bilibili.com/7734200", false)]
    public void CanHandle_OnlyDouyinLinks(string url, bool expected)
    {
        using var http = DouyinTestData.CreateHttpClient(new DouyinTestData.StubHandler(_ => DouyinTestData.HomepageResponse()));
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);
        Assert.Equal(expected, provider.CanHandle(url));
    }

    [Fact]
    public void ExtractRoomId_Delegates()
    {
        using var http = DouyinTestData.CreateHttpClient(new DouyinTestData.StubHandler(_ => DouyinTestData.HomepageResponse()));
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);
        Assert.Equal("141601416352", provider.ExtractRoomId("https://live.douyin.com/141601416352"));
        Assert.Throws<ArgumentException>(() => provider.ExtractRoomId("https://example.com/1"));
    }

    [Fact]
    public async Task GetRoomStatusAsync_LiveRoom_BuildsQualities()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);

        var status = await provider.GetRoomStatusAsync("141601416352");

        Assert.True(status.IsLive);
        Assert.Equal(PlatformType.Douyin, status.Platform);
        Assert.Equal("141601416352", status.RoomId);
        Assert.Equal("看海老师", status.AnchorName);
        Assert.Equal(4, status.AvailableQualities.Count);
    }

    [Fact]
    public async Task GetRoomStatusAsync_OfflineRoom_ReturnsOffline()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.OfflineJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);

        var status = await provider.GetRoomStatusAsync("7555682083511913256");

        Assert.False(status.IsLive);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public async Task GetRoomStatusAsync_AcceptsLink()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        var provider = new DouyinProvider(api);

        var status = await provider.GetRoomStatusAsync("https://live.douyin.com/141601416352");

        Assert.True(status.IsLive);
        Assert.Equal("141601416352", status.RoomId);
    }
}
