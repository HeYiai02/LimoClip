using System.Net;
using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuApiClientTests
{
    private static DouyuApiClient CreateClient(TestHttpMessageHandler handler, Dictionary<string, string>? signature = null)
    {
        signature ??= new Dictionary<string, string>
        {
            ["v"] = "220120261123",
            ["did"] = "0123456789abcdef0123456789abcdef",
            ["tt"] = "1700000000",
            ["sign"] = "abcdef0123456789abcdef0123456789",
        };
        return new DouyuApiClient(
            DouyuTestData.CreateClient(handler),
            (roomId, ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(signature));
    }

    [Fact]
    public async Task GetRoomAsync_Live_ResturnsMetadataAndQuality()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.Contains("betard", StringComparison.Ordinal)
                ? DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true))
                : DouyuTestData.JsonResponse(DouyuTestData.StreamJson());
        });

        using var client = CreateClient(handler);
        var snapshot = await client.GetRoomAsync("9999");

        Assert.True(snapshot.IsLive);
        Assert.Equal("9999", snapshot.RoomId);
        Assert.Equal("陪伴每一天！", snapshot.RoomName);
        Assert.Equal("yyfyyf", snapshot.Nick);
        Assert.Equal(204389, snapshot.OwnerUid);
        var quality = Assert.Single(snapshot.Qualities);
        Assert.Equal(PlatformType.Douyu, PlatformType.Douyu);
        Assert.Equal("douyu-4", quality.Id);
        Assert.Equal("蓝光4M", quality.DisplayName);
        Assert.Equal("https://stream.example/live/abc_4000.flv?wsAuth=x&token=y", quality.StreamUrl);
        Assert.Equal("https://www.douyu.com/9999", quality.RequiredHeaders["Referer"]);
        Assert.Contains("Mozilla", quality.RequiredHeaders["User-Agent"]);

        var betard = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("betard"));
        Assert.Contains("9999", betard.RequestUri!.AbsolutePath);
        var play = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("getH5Play"));
        Assert.Equal(HttpMethod.Post, play.Method);
    }

    [Fact]
    public async Task GetRoomAsync_Offline_DoesNotRequestStream()
    {
        var handler = new TestHttpMessageHandler(_ =>
            DouyuTestData.JsonResponse(DouyuTestData.BetardJson(false)));

        using var client = CreateClient(handler);
        var snapshot = await client.GetRoomAsync("9999");

        Assert.False(snapshot.IsLive);
        Assert.Empty(snapshot.Qualities);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("getH5Play"));
    }

    [Fact]
    public async Task GetRoomAsync_StreamError_ThrowsFriendlyMessage()
    {
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("betard", StringComparison.Ordinal)
                ? DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true))
                : DouyuTestData.JsonResponse(DouyuTestData.StreamJson(-9, "时间戳错误")));

        using var client = CreateClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRoomAsync("9999"));
        Assert.Contains("获取播放地址失败", error.Message);
        Assert.Contains("-9", error.Message);
    }

    [Fact]
    public async Task GetRoomAsync_GzipBetardJson_IsDecompressed()
    {
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("betard", StringComparison.Ordinal)
                ? DouyuTestData.GzipJsonResponse(DouyuTestData.BetardJson(true))
                : DouyuTestData.JsonResponse(DouyuTestData.StreamJson()));

        using var client = CreateClient(handler);
        var snapshot = await client.GetRoomAsync("9999");
        Assert.True(snapshot.IsLive);
        Assert.Equal("陪伴每一天！", snapshot.RoomName);
    }

    [Fact]
    public async Task GetRoomAsync_RejectsNonNumericRoom()
    {
        var handler = new TestHttpMessageHandler(_ =>
            throw new InvalidOperationException("不应发起 HTTP 请求"));
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetRoomAsync("https://www.douyu.com/9999"));
    }

    [Fact]
    public async Task GetRoomAsync_MissingRoomObject_Throws()
    {
        var handler = new TestHttpMessageHandler(_ =>
            DouyuTestData.JsonResponse("{\"data\":{}}"));
        using var client = CreateClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRoomAsync("9999"));
        Assert.Contains("缺少 room", error.Message);
    }

    [Fact]
    public async Task GetRoomAsync_SignatureValues_AreSentToGetH5Play()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("betard", StringComparison.Ordinal))
            {
                return DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true));
            }

            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("rate=-1", body);
            Assert.Contains("sign=abcdef0123456789abcdef0123456789", body);
            return DouyuTestData.JsonResponse(DouyuTestData.StreamJson());
        });

        using var client = CreateClient(handler);
        await client.GetRoomAsync("9999");
    }

    [Fact]
    public async Task GetRoomAsync_WithCookie_SendsCookieToApiAndQualityHeaders()
    {
        const string cookie = "dy_auth=token123; acf_uid=42";
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("betard", StringComparison.Ordinal)
                ? DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true))
                : DouyuTestData.JsonResponse(DouyuTestData.StreamJson()));

        using var client = CreateClient(handler);
        var snapshot = await client.GetRoomAsync("9999", cookie);

        var quality = Assert.Single(snapshot.Qualities);
        Assert.Equal(cookie, quality.RequiredHeaders["Cookie"]);

        var betard = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("betard"));
        Assert.Equal(cookie, Assert.Single(betard.Headers.GetValues("Cookie")));

        var play = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("getH5Play"));
        Assert.Equal(cookie, Assert.Single(play.Headers.GetValues("Cookie")));
    }

    [Fact]
    public async Task GetRoomAsync_WithoutCookie_SendsNoCookieHeader()
    {
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("betard", StringComparison.Ordinal)
                ? DouyuTestData.JsonResponse(DouyuTestData.BetardJson(true))
                : DouyuTestData.JsonResponse(DouyuTestData.StreamJson()));

        using var client = CreateClient(handler);
        var snapshot = await client.GetRoomAsync("9999");

        var quality = Assert.Single(snapshot.Qualities);
        Assert.False(quality.RequiredHeaders.ContainsKey("Cookie"));
        Assert.All(handler.Requests, r => Assert.False(r.Headers.Contains("Cookie")));
    }
}
