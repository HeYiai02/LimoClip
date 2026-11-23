using LiveMonitor.Plugins.Huya;
using System.Net.WebSockets;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaDanmakuClientTests
{
    [Fact]
    public async Task ConnectAsync_SendsSubscribeAndDispatchesChatMessage()
    {
        using var http = HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson));
        var socket = new FakeWebSocket();
        var client = new HuyaDanmakuClient("660000", httpClient: http, socketFactory: () => socket);
        var received = new TaskCompletionSource<DanmakuMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);
        try
        {
            await client.ConnectAsync();

            await WaitUntilAsync(() => socket.Sent.Count > 0, TimeSpan.FromSeconds(5));
            var subscribe = Assert.Single(socket.Sent);
            Assert.Equal(HuyaPacketParser.BuildSubscribe(1346609715), subscribe);

            socket.Push(HuyaTestData.BuildPushFrame(1400, HuyaTestData.BuildChatBody("123", "观众A", "hello")));
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(PlatformType.Huya, message.Platform);
            Assert.Equal("660000", message.RoomId);
            Assert.Equal("123", message.SenderUid);
            Assert.Equal("观众A", message.SenderName);
            Assert.Equal("hello", message.Content);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_OfflineRoom_ThrowsBeforeCreatingSocket()
    {
        using var http = HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.OfflineJson));
        var socketCreated = false;
        var client = new HuyaDanmakuClient(
            "660000",
            httpClient: http,
            socketFactory: () =>
            {
                socketCreated = true;
                return new FakeWebSocket();
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
        Assert.False(socketCreated);
    }

    [Fact]
    public async Task Disconnected_RaisedWhenServerCloses()
    {
        using var http = HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson));
        var socket = new FakeWebSocket();
        var client = new HuyaDanmakuClient("660000", httpClient: http, socketFactory: () => socket);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);
        try
        {
            await client.ConnectAsync();
            await WaitUntilAsync(() => socket.Sent.Count > 0, TimeSpan.FromSeconds(5));

            socket.PushClose();
            var error = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(error);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectAsync_DoesNotRaiseDisconnected()
    {
        using var http = HuyaTestData.CreateHttpClient(_ => HuyaTestData.Json(HuyaTestData.LiveJson));
        var socket = new FakeWebSocket();
        var client = new HuyaDanmakuClient("660000", httpClient: http, socketFactory: () => socket);
        var disconnectedCount = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnectedCount);
        try
        {
            await client.ConnectAsync();
            await WaitUntilAsync(() => socket.Sent.Count > 0, TimeSpan.FromSeconds(5));

            await client.DisconnectAsync();
            await Task.Delay(150);

            Assert.Equal(0, Volatile.Read(ref disconnectedCount));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => new HuyaDanmakuClient("  "));
        Assert.Throws<ArgumentException>(() => new HuyaDanmakuClient("660000", authCookie: "a=1\r\nb=2"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(false, "等待条件超时。");
    }

    /// <summary>可注入帧的假 WebSocket：ConnectAsync 由外部完成，SendAsync 记录已发送字节。</summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly object _gate = new();
        private readonly Queue<(byte[] Frame, bool Close)> _incoming = new();
        private readonly SemaphoreSlim _signal = new(0);
        private WebSocketState _state = WebSocketState.Open;

        public List<byte[]> Sent { get; } = new();

        public void Push(byte[] frame)
        {
            lock (_gate)
            {
                _incoming.Enqueue((frame, false));
            }

            _signal.Release();
        }

        public void PushClose()
        {
            lock (_gate)
            {
                _incoming.Enqueue((Array.Empty<byte>(), true));
            }

            _signal.Release();
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _signal.Dispose();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            (byte[] Frame, bool Close) item;
            lock (_gate)
            {
                item = _incoming.Dequeue();
            }

            if (item.Close)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var count = Math.Min(item.Frame.Length, buffer.Count);
            Array.Copy(item.Frame, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, item.Frame.Length <= buffer.Count);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Sent.Add(buffer.ToArray());
            }

            return Task.CompletedTask;
        }
    }
}
