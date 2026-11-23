using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// BilibiliDanmakuClient 的端到端（进程内）测试：用 fake WebSocket 连接与
/// <see cref="TestHttpMessageHandler"/> 替代真实网络，注入 delay/clock 保证确定性。
/// </summary>
public sealed class BilibiliDanmakuClientTests
{
    private const string RoomId = "1234";
    private const string Cookie = "buvid3=cookie-buvid; DedeUserID=42";
    private const string DanmuInfoHost = "broadcastlv.chat.bilibili.com";

    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public async Task ConnectAsync_ConnectsToWssSubAndSendsAuthPacketWithCookieIdentity()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new Uri($"wss://{DanmuInfoHost}:443/sub"),
            socket.ConnectedUri);

        var auth = await socket.AuthorizationSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BilibiliPacketParser.OpClientAuth, ReadOperation(auth));
        Assert.Equal(BilibiliPacketParser.ProtoHeartbeat, ReadProtoVersion(auth));

        var json = Encoding.UTF8.GetString(auth, BilibiliPacketParser.HeaderSize, auth.Length - BilibiliPacketParser.HeaderSize);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(42, root.GetProperty("uid").GetInt64());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("roomid").ValueKind);
        Assert.Equal(1234, root.GetProperty("roomid").GetInt64());
        Assert.Equal(3, root.GetProperty("protover").GetInt32());
        Assert.Equal("cookie-buvid", root.GetProperty("buvid").GetString());
        Assert.Equal("web", root.GetProperty("platform").GetString());
        Assert.Equal(2, root.GetProperty("type").GetInt32());
        Assert.Equal("token-from-danmuinfo", root.GetProperty("key").GetString());

        var danmuInfoRequest = Assert.Single(handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("getDanmuInfo", StringComparison.Ordinal))
            .ToArray());
        Assert.Contains("id=1234&type=0", danmuInfoRequest.RequestUri!.Query);
        Assert.Equal(
            PlatformDefaults.UserAgent,
            string.Join(" ", danmuInfoRequest.Headers.GetValues("User-Agent")));
        Assert.Equal(
            new[] { "https://live.bilibili.com/" },
            danmuInfoRequest.Headers.GetValues("Referer"));
        Assert.Equal(
            new[] { "application/json", "text/plain", "*/*" },
            danmuInfoRequest.Headers.GetValues("Accept"));
        Assert.Equal(new[] { Cookie }, danmuInfoRequest.Headers.GetValues("Cookie"));
        Assert.Equal(new[] { "https://live.bilibili.com" }, danmuInfoRequest.Headers.GetValues("Origin"));

        // 已有 buvid3 时不应请求 spi 接口。
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri!.AbsolutePath.Contains("finger/spi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_SignsGetDanmuInfoWithWbiSignature()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var danmuInfoRequest = Assert.Single(handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("getDanmuInfo", StringComparison.Ordinal))
            .ToArray());
        Assert.Equal(
            "?id=1234&type=0&web_location=444.8&wts=1778051289&w_rid=6fda64157c113669005be917dc22e9d8",
            danmuInfoRequest.RequestUri!.Query);

        // 签名密钥来自 nav 接口的 wbi_img。
        Assert.Single(handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("web-interface/nav", StringComparison.Ordinal))
            .ToArray());
    }

    [Fact]
    public async Task ConnectAsync_WbiKeysUnavailable_ThrowsWithWbiMessage()
    {
        var socket = new FakeBilibiliWebSocketConnection();

        using var handler = new ConfiguredHandler(
            navJson: """{"code":-101,"message":"账号未登录","data":null}""");
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("WBI", exception.Message);
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task ConnectAsync_PrefersWssPortOverWsAndLegacyPort()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler(
            danmuInfoJson: """
                {"code":0,"message":"0","data":{"token":"t","host_list":[
                    {"host":"plain.example.com","ws_port":2244,"port":2243},
                    {"host":"secure.example.com","wss_port":443},
                    {"host":"also.example.com","wss_port":8443}]}}
                """);
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new Uri("wss://secure.example.com:443/sub"), socket.ConnectedUri);
    }

    [Fact]
    public async Task ConnectAsync_FallsBackToWsPortWhenWssPortMissing()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler(
            danmuInfoJson: """
                {"code":0,"message":"0","data":{"token":"t","host_list":[
                    {"host":"plain.example.com","ws_port":2244,"port":2243}]}}
                """);
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new Uri("wss://plain.example.com:2244/sub"), socket.ConnectedUri);
    }

    [Fact]
    public async Task ConnectAsync_NoCookie_UsesBuvidFromSpiAndAnonymousUid()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket, cookie: null);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("finger/spi", StringComparison.Ordinal))
            .ToArray());

        var auth = await socket.AuthorizationSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = Encoding.UTF8.GetString(auth, BilibiliPacketParser.HeaderSize, auth.Length - BilibiliPacketParser.HeaderSize);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("uid").GetInt64());
        Assert.Equal("spi-buvid-3", document.RootElement.GetProperty("buvid").GetString());
    }

    [Fact]
    public async Task ConnectAsync_SpiFailure_DegradesToEmptyBuvidWithoutBlocking()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler(spiStatusCode: HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket, cookie: null);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var auth = await socket.AuthorizationSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = Encoding.UTF8.GetString(auth, BilibiliPacketParser.HeaderSize, auth.Length - BilibiliPacketParser.HeaderSize);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(string.Empty, document.RootElement.GetProperty("buvid").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("uid").GetInt64());
    }

    [Fact]
    public async Task ConnectAsync_AuthReplyWithNonZeroCode_Throws()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(-101, "未登录")
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("-101", exception.Message);
        Assert.Contains("未登录", exception.Message);
        Assert.True(socket.DisposeCount >= 1);
    }

    [Fact]
    public async Task ConnectAsync_PeerClosesBeforeAuthReply_Throws()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.CloseFromPeer();

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("认证完成前", exception.Message);
    }

    [Fact]
    public async Task ConnectAsync_HttpError_ThrowsHttpRequestException()
    {
        var socket = new FakeBilibiliWebSocketConnection();

        using var handler = new ConfiguredHandler(danmuInfoStatusCode: HttpStatusCode.BadGateway);
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task ConnectAsync_DanmuInfoNonZeroCode_ThrowsWithCodeAndMessage()
    {
        var socket = new FakeBilibiliWebSocketConnection();

        using var handler = new ConfiguredHandler(
            danmuInfoJson: """{"code":-400,"message":"房间不存在","data":null}""");
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("-400", exception.Message);
        Assert.Contains("房间不存在", exception.Message);
    }

    [Fact]
    public async Task NotificationFrame_RaisesMessageReceived()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var received = new TaskCompletionSource<DanmakuMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        socket.Push(DanmakuFrame("后台弹幕", "88", "dave"));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlatformType.Bilibili, message.Platform);
        Assert.Equal(RoomId, message.RoomId);
        Assert.Equal("88", message.SenderUid);
        Assert.Equal("dave", message.SenderName);
        Assert.Equal("后台弹幕", message.Content);
        Assert.Equal(FixedTime, message.Timestamp);
        Assert.False(message.IsGiftOrSuperChat);
    }

    [Fact]
    public async Task AuthReplyFrameWithDanmaku_DeliversMessageBeforeReturning()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? Concat(AuthReply(0), DanmakuFrame("握手夹带", "6", "frank"))
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var received = new TaskCompletionSource<DanmakuMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("握手夹带", message.Content);
    }

    [Fact]
    public async Task PeerClose_RaisesDisconnectedWithNull()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        socket.CloseFromPeer();

        var error = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(error);
    }

    [Fact]
    public async Task ReceiveFailure_RaisesDisconnectedWithException()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        socket.FailFromPeer(new IOException("boom"));

        var error = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task HeartbeatLoop_SendsOp2PacketWithObjectObjectBody()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);

        var intervals = new List<TimeSpan>();
        var delayCalls = 0;
        Func<TimeSpan, CancellationToken, Task> delay = async (interval, ct) =>
        {
            lock (intervals)
            {
                intervals.Add(interval);
            }

            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };

        await using var client = CreateClient(http, socket, delay: delay);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var heartbeat = await socket.HeartbeatSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BilibiliPacketParser.OpClientHeartbeat, ReadOperation(heartbeat));
        Assert.Equal(
            "[object Object]",
            Encoding.UTF8.GetString(heartbeat, BilibiliPacketParser.HeaderSize, heartbeat.Length - BilibiliPacketParser.HeaderSize));
        lock (intervals)
        {
            Assert.NotEmpty(intervals);
            Assert.All(intervals, interval => Assert.Equal(TimeSpan.FromSeconds(30), interval));
        }
    }

    [Fact]
    public async Task DisconnectAsync_DoesNotRaiseDisconnectedAndIsIdempotent()
    {
        var socket = new FakeBilibiliWebSocketConnection();
        socket.RespondToSend = packet => IsOperation(packet, BilibiliPacketParser.OpClientAuth)
            ? AuthReply(0)
            : null;

        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);
        await using var client = CreateClient(http, socket);

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.Aborted);
        Assert.False(disconnected.Task.IsCompleted);

        // 重复 Disconnect/Dispose 不抛且不重复触发事件。
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(disconnected.Task.IsCompleted);
        Assert.True(socket.DisposeCount >= 1);
    }

    [Fact]
    public void Constructor_RejectsNonNumericRoomId()
    {
        using var handler = new ConfiguredHandler();
        using var http = new HttpClient(handler);

        Assert.Throws<ArgumentException>(() => new BilibiliDanmakuClient("abc", null, http));
        Assert.Throws<ArgumentException>(() => new BilibiliDanmakuClient("0", null, http));
    }

    private static BilibiliDanmakuClient CreateClient(
        HttpClient http,
        FakeBilibiliWebSocketConnection socket,
        string? cookie = Cookie,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(
            RoomId,
            cookie,
            http,
            () => socket,
            delay ?? InfiniteDelay,
            () => FixedTime);

    private static Task InfiniteDelay(TimeSpan interval, CancellationToken cancellationToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static byte[] AuthReply(int code, string? message = null)
    {
        var json = message is null
            ? $"{{\"code\":{code}}}"
            : $"{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}}}";
        return BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpAuthReply,
            json,
            BilibiliPacketParser.ProtoHeartbeat);
    }

    private static byte[] DanmakuFrame(string content, string uid, string uname)
    {
        var json = $"{{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16777215],{JsonSerializer.Serialize(content)},[{uid},{JsonSerializer.Serialize(uname)},0,0]]}}";
        return BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            json,
            BilibiliPacketParser.ProtoJson);
    }

    private static bool IsOperation(byte[] packet, int operation)
        => ReadOperation(packet) == operation;

    private static short ReadProtoVersion(byte[] packet)
        => BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(6, 2));

    private static int ReadOperation(byte[] packet)
        => BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4));

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>nav（WBI 密钥）/ getDanmuInfo / finger spi 三个接口的可配置假 HTTP 处理器。</summary>
    private sealed class ConfiguredHandler : HttpMessageHandler
    {
        private const string DefaultNavJson = """
            {"code":-101,"message":"账号未登录","data":{"isLogin":false,"wbi_img":{
                "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
            """;

        private readonly string _danmuInfoJson;
        private readonly HttpStatusCode _danmuInfoStatusCode;
        private readonly string _spiJson;
        private readonly HttpStatusCode _spiStatusCode;
        private readonly string _navJson;
        private readonly HttpStatusCode _navStatusCode;
        private readonly List<HttpRequestMessage> _requests = new();

        public ConfiguredHandler(
            string? danmuInfoJson = null,
            HttpStatusCode danmuInfoStatusCode = HttpStatusCode.OK,
            string? spiJson = null,
            HttpStatusCode spiStatusCode = HttpStatusCode.OK,
            string? navJson = null,
            HttpStatusCode navStatusCode = HttpStatusCode.OK)
        {
            _danmuInfoJson = danmuInfoJson ?? """
                {"code":0,"message":"0","data":{"token":"token-from-danmuinfo","host_list":[
                    {"host":"broadcastlv.chat.bilibili.com","wss_port":443,"ws_port":2244,"port":2243}]}}
                """;
            _danmuInfoStatusCode = danmuInfoStatusCode;
            _spiJson = spiJson ?? """{"code":0,"data":{"b_3":"spi-buvid-3"}}""";
            _spiStatusCode = spiStatusCode;
            _navJson = navJson ?? DefaultNavJson;
            _navStatusCode = navStatusCode;
        }

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var response = path.Contains("web-interface/nav", StringComparison.Ordinal)
                ? TestHttpMessageHandler.Json(_navJson, _navStatusCode)
                : path.Contains("getDanmuInfo", StringComparison.Ordinal)
                    ? TestHttpMessageHandler.Json(_danmuInfoJson, _danmuInfoStatusCode)
                    : path.Contains("finger/spi", StringComparison.Ordinal)
                        ? TestHttpMessageHandler.Json(_spiJson, _spiStatusCode)
                        : TestHttpMessageHandler.Json("""{"code":-404,"message":"unexpected request"}""", HttpStatusCode.NotFound);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}

/// <summary>进程内假 WebSocket：记录发送包、可回包、可模拟对端关闭/异常。</summary>
internal sealed class FakeBilibiliWebSocketConnection : IBilibiliWebSocketConnection
{
    private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
    private readonly object _sync = new();
    private readonly List<byte[]> _sent = new();
    private int _disposeCount;

    public Uri? ConnectedUri { get; private set; }

    public bool Aborted { get; private set; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public TaskCompletionSource<byte[]> AuthorizationSent { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<byte[]> HeartbeatSent { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>收到客户端包时返回要回给客户端的一帧；null 表示不回。</summary>
    public Func<byte[], byte[]?>? RespondToSend { get; set; }

    public IReadOnlyList<byte[]> Sent
    {
        get
        {
            lock (_sync)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var packet = payload.ToArray();
        lock (_sync)
        {
            _sent.Add(packet);
        }

        var operation = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4));
        if (operation == BilibiliPacketParser.OpClientAuth)
        {
            AuthorizationSent.TrySetResult(packet);
        }
        else if (operation == BilibiliPacketParser.OpClientHeartbeat)
        {
            HeartbeatSent.TrySetResult(packet);
        }

        var reply = RespondToSend?.Invoke(packet);
        if (reply is not null)
        {
            _incoming.Writer.TryWrite(reply);
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        => await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public void Abort()
    {
        Aborted = true;
        _incoming.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>向客户端推送一帧数据。</summary>
    public void Push(byte[] frame) => _incoming.Writer.TryWrite(frame);

    /// <summary>模拟对端正常关闭（Close 帧）。</summary>
    public void CloseFromPeer() => _incoming.Writer.TryWrite(null);

    /// <summary>模拟接收路径异常（网络错误）。</summary>
    public void FailFromPeer(Exception error) => _incoming.Writer.TryComplete(error);
}
