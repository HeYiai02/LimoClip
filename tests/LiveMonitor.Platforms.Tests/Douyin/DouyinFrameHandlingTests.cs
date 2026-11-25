using System.Net.WebSockets;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

/// <summary>
/// 覆盖抖音弹幕内部帧处理：损坏 GZip 容错、needAck 回执、心跳/回执帧忽略、聊天消息分发。
/// 通过 internal 构造注入假连接，直接驱动 <see cref="DouyinDanmakuClient.HandleFrameAsync"/>，不建立真实网络连接。
/// </summary>
public sealed class DouyinFrameHandlingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HandleFrameAsync_CorruptGzipPayload_IsIgnoredWithoutSideEffects()
    {
        await using var host = await FrameTestHost.CreateAsync();
        var messages = new List<DanmakuMessage>();
        host.Client.MessageReceived += messages.Add;

        // payload 不是合法 gzip：解压失败只应被静默忽略，既不抛错也不触发回执。
        var corruptPayload = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var frame = DouyinProto.BuildPushFrame("msg", corruptPayload, 9UL);

        await host.Client.HandleFrameAsync(frame, CancellationToken.None).WaitAsync(WaitTimeout);

        Assert.Empty(messages);
        Assert.DoesNotContain(host.Connection.SentFrames(), f => f.PayloadType == "ack");
    }

    [Fact]
    public async Task HandleFrameAsync_HeartbeatAndAckFrames_AreIgnored()
    {
        await using var host = await FrameTestHost.CreateAsync();
        var messages = new List<DanmakuMessage>();
        host.Client.MessageReceived += messages.Add;

        // 服务端心跳/回执帧没有业务负载，不应被当作响应解压，也不应触发二次回执。
        var heartbeat = DouyinProto.BuildPushFrame("hb", Array.Empty<byte>(), 0UL);
        var ack = DouyinProto.BuildPushFrame("ack", new byte[] { 1, 2, 3 }, 11UL);

        await host.Client.HandleFrameAsync(heartbeat, CancellationToken.None).WaitAsync(WaitTimeout);
        await host.Client.HandleFrameAsync(ack, CancellationToken.None).WaitAsync(WaitTimeout);

        Assert.Empty(messages);
        Assert.DoesNotContain(host.Connection.SentFrames(), f => f.PayloadType == "ack");
    }

    [Fact]
    public async Task HandleFrameAsync_NeedAck_SendsAckWithInternalExtAndLogId()
    {
        await using var host = await FrameTestHost.CreateAsync();
        const ulong logId = 777UL;
        const string internalExt = "internal-ext-token";
        var response = DouyinTestData.BuildResponse(
            Array.Empty<(string Method, byte[] Payload)>(),
            internalExt,
            true);
        var frame = DouyinProto.BuildPushFrame("msg", DouyinTestData.Gzip(response), logId);

        await host.Client.HandleFrameAsync(frame, CancellationToken.None).WaitAsync(WaitTimeout);

        var ack = Assert.Single(host.Connection.SentFrames(), f => f.PayloadType == "ack");
        Assert.Equal(logId, ack.LogId);
        Assert.Equal(internalExt, Encoding.UTF8.GetString(ack.Payload));
    }

    [Fact]
    public async Task HandleFrameAsync_ChatMessage_RaisesMessageReceived()
    {
        await using var host = await FrameTestHost.CreateAsync();
        var messages = new List<DanmakuMessage>();
        host.Client.MessageReceived += messages.Add;

        var response = DouyinTestData.BuildResponse(
            new (string Method, byte[] Payload)[]
            {
                ("WebcastChatMessage", DouyinTestData.BuildChatPayload(42UL, "Alice", "hello")),
            },
            string.Empty,
            false);
        var frame = DouyinProto.BuildPushFrame("msg", DouyinTestData.Gzip(response), 5UL);

        await host.Client.HandleFrameAsync(frame, CancellationToken.None).WaitAsync(WaitTimeout);

        var message = Assert.Single(messages);
        Assert.Equal(PlatformType.Douyin, message.Platform);
        Assert.Equal("141601416352", message.RoomId);
        Assert.Equal("42", message.SenderUid);
        Assert.Equal("Alice", message.SenderName);
        Assert.Equal("hello", message.Content);
        Assert.False(message.IsGiftOrSuperChat);
        Assert.DoesNotContain(host.Connection.SentFrames(), f => f.PayloadType == "ack");
    }

    [Fact]
    public async Task HandleFrameAsync_EmptyPayload_IsIgnored()
    {
        await using var host = await FrameTestHost.CreateAsync();
        var messages = new List<DanmakuMessage>();
        host.Client.MessageReceived += messages.Add;

        var frame = DouyinProto.BuildPushFrame("msg", Array.Empty<byte>(), 2UL);

        await host.Client.HandleFrameAsync(frame, CancellationToken.None).WaitAsync(WaitTimeout);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task HandleFrameAsync_NeedAck_WhenConnectionClosed_DoesNotThrowOrSend()
    {
        await using var host = await FrameTestHost.CreateAsync();
        host.Connection.SetState(WebSocketState.Closed);

        var response = DouyinTestData.BuildResponse(
            Array.Empty<(string Method, byte[] Payload)>(),
            "ext",
            true);
        var frame = DouyinProto.BuildPushFrame("msg", DouyinTestData.Gzip(response), 3UL);

        await host.Client.HandleFrameAsync(frame, CancellationToken.None).WaitAsync(WaitTimeout);

        Assert.DoesNotContain(host.Connection.SentFrames(), f => f.PayloadType == "ack");
    }

    private sealed class FrameTestHost : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly DouyinApiClient _api;
        private readonly DouyinSigner _signer;

        private FrameTestHost(
            HttpClient http,
            DouyinApiClient api,
            DouyinSigner signer,
            DouyinDanmakuClient client,
            RecordingConnection connection)
        {
            _http = http;
            _api = api;
            _signer = signer;
            Client = client;
            Connection = connection;
        }

        public DouyinDanmakuClient Client { get; }

        public RecordingConnection Connection { get; }

        public static async Task<FrameTestHost> CreateAsync()
        {
            var scriptPath = DouyinTestData.CreateStubSignScript();
            var handler = new DouyinTestData.StubHandler(request =>
                request.RequestUri!.AbsolutePath == "/"
                    ? DouyinTestData.HomepageResponse()
                    : DouyinTestData.JsonResponse(DouyinTestData.LiveJson));
            var http = DouyinTestData.CreateHttpClient(handler);
            var api = new DouyinApiClient(http);
            var signer = new DouyinSigner(scriptPath: scriptPath, cacheDirectory: Path.GetDirectoryName(scriptPath)!);
            var connection = new RecordingConnection();
            var client = new DouyinDanmakuClient(
                "141601416352",
                null,
                null,
                api,
                signer,
                () => connection);
            await client.ConnectAsync().WaitAsync(WaitTimeout);
            return new FrameTestHost(http, api, signer, client, connection);
        }

        public async ValueTask DisposeAsync()
        {
            // 客户端只释放一次：重复 Dispose 会在已释放的 CancellationTokenSource 上 Cancel 而抛 ObjectDisposedException。
            await Client.DisposeAsync();
            _signer.Dispose();
            _api.Dispose();
            _http.Dispose();
        }
    }

    private sealed class RecordingConnection : IDouyinWebSocketConnection
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _sent = new();
        private volatile WebSocketState _state = WebSocketState.Open;

        public WebSocketState State => _state;

        public IReadOnlyList<DouyinProto.PushFrameData> SentFrames()
        {
            byte[][] raw;
            lock (_gate) raw = _sent.ToArray();
            return raw.Select(DouyinProto.ParsePushFrame).ToArray();
        }

        public void SetState(WebSocketState state) => _state = state;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
            => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            lock (_gate) _sent.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReceiveMessageAsync(CancellationToken ct)
        {
            // 假连接不产生下行数据，保持挂起直到取消（由客户端生命周期令牌控制）。
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return null;
        }

        public void Abort() => _state = WebSocketState.Aborted;

        public ValueTask DisposeAsync()
        {
            _state = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
