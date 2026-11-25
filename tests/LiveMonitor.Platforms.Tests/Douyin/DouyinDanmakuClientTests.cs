using System.Net.WebSockets;
using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinDanmakuClientTests
{
    [Fact]
    public async Task Constructor_NormalizesLinkToRoomId()
    {
        using var http = DouyinTestData.CreateHttpClient(new DouyinTestData.StubHandler(_ => DouyinTestData.HomepageResponse()));
        using var api = new DouyinApiClient(http);
        await using var client = new DouyinDanmakuClient("https://live.douyin.com/141601416352", api: api);
        Assert.Equal("141601416352", client.RoomId);
        Assert.Equal(PlatformType.Douyin, client.Platform);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/123456")]
    public void Constructor_InvalidRoom_Throws(string roomId)
    {
        Assert.Throws<ArgumentException>(() => new DouyinDanmakuClient(roomId));
    }

    [Fact]
    public void Constructor_CookieWithNewline_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DouyinDanmakuClient("141601416352", "a=b\r\nc=d"));
        Assert.Contains("Cookie", exception.Message);
    }

    [Fact]
    public void BuildWebSocketUrl_ContainsExpectedParameters()
    {
        var url = DouyinDanmakuClient.BuildWebSocketUrl("7711351763655969576", "1234567890123456", 1700000000000);

        Assert.StartsWith("wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/", url, StringComparison.Ordinal);
        Assert.Contains("room_id=7711351763655969576", url, StringComparison.Ordinal);
        Assert.Contains("user_unique_id=1234567890123456", url, StringComparison.Ordinal);
        Assert.Contains("identity=audience", url, StringComparison.Ordinal);
        Assert.Contains("webcast_sdk_version=1.0.14-beta.0", url, StringComparison.Ordinal);
        Assert.Contains("compress=gzip", url, StringComparison.Ordinal);
        Assert.Contains("wss_push_room_id:7711351763655969576", url, StringComparison.Ordinal);
        Assert.Contains("first_req_ms:1700000000000", url, StringComparison.Ordinal);
        Assert.DoesNotContain("signature=", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_OfflineRoom_ThrowsChineseError()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.OfflineJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        await using var client = new DouyinDanmakuClient("7555682083511913256", api: api);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
        Assert.Contains("未开播", exception.Message);
    }

    [Fact]
    public async Task ConnectAsync_MissingSignScript_ThrowsChineseError()
    {
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        var missingDir = Path.Combine(Path.GetTempPath(), "limoclip-douyin-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(missingDir);
        var signer = new DouyinSigner(
            httpClient: new HttpClient(new ThrowingHandler()),
            scriptPath: Path.Combine(missingDir, "missing.js"),
            cacheDirectory: missingDir);
        await using var client = new DouyinDanmakuClient("141601416352", api: api, signer: signer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
        Assert.Contains("sign.js", exception.Message);
    }

    [Fact]
    public async Task DisconnectAsync_BeforeConnect_IsSafe()
    {
        await using var client = new DouyinDanmakuClient("141601416352");
        await client.DisconnectAsync();
        Assert.Equal(WebSocketState.None, WebSocketState.None);
    }

    [Fact]
    public async Task HeartbeatSendFailure_RaisesDisconnectedExactlyOnce()
    {
        var scriptPath = DouyinTestData.CreateStubSignScript();
        var handler = new DouyinTestData.StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/"
                ? DouyinTestData.HomepageResponse()
                : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
        using var http = DouyinTestData.CreateHttpClient(handler);
        using var api = new DouyinApiClient(http);
        using var signer = new DouyinSigner(scriptPath: scriptPath, cacheDirectory: Path.GetDirectoryName(scriptPath)!);
        var connection = new FailingSendConnection();
        await using var client = new DouyinDanmakuClient(
            "141601416352",
            null,
            null,
            api,
            signer,
            () => connection);

        var disconnected = 0;
        Exception? captured = null;
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += exception =>
        {
            Interlocked.Increment(ref disconnected);
            captured = exception;
            raised.TrySetResult();
        };

        await client.ConnectAsync();

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(raised.Task, completed);

        await client.DisconnectAsync();

        Assert.Equal(1, Volatile.Read(ref disconnected));
        var failure = Assert.IsType<WebSocketException>(captured);
        Assert.Contains("simulated heartbeat failure", failure.Message, StringComparison.Ordinal);
    }

    private sealed class FailingSendConnection : IDouyinWebSocketConnection
    {
        public WebSocketState State => WebSocketState.Open;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
            => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => Task.FromException(new WebSocketException("simulated heartbeat failure"));

        public async Task<byte[]?> ReceiveMessageAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }

        public void Abort()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
