using System.IO;
using System.Net.WebSockets;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙实时弹幕客户端（网页版 webh5 信令）：连 <c>wss://wsapi.huya.com</c>，命令 16 订阅
/// <c>live:&lt;主播uid&gt;</c> / <c>chat:&lt;主播uid&gt;</c> 两个消息组，之后由服务端推送 uri 1400 弹幕。
/// <para>
/// 服务端不要求应用层心跳（网页版命令 33 为可选保活），连接由 WebSocket ping 与持续的消息流维持；
/// 因此这里只做接收循环。断线重连统一交给 <c>DanmakuManager</c>：<see cref="ConnectAsync"/> 只尝试一次。
/// </para>
/// </summary>
public sealed class HuyaDanmakuClient : IDanmakuClient
{
    private readonly string _roomId;
    private readonly HuyaApiClient _api;
    private readonly HttpClient? _ownedHttpClient;
    private readonly Func<WebSocket>? _socketFactory;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int _connectState;
    private volatile bool _disconnectRequested;

    public HuyaDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null,
        Func<WebSocket>? socketFactory = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("虎牙房间号不能为空。", nameof(roomId));
        }

        if (authCookie is not null && (authCookie.Contains('\r') || authCookie.Contains('\n')))
        {
            throw new ArgumentException("Cookie 不能包含换行符。", nameof(authCookie));
        }

        _roomId = roomId.Trim();
        if (httpClient is null)
        {
            _ownedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _api = new HuyaApiClient(_ownedHttpClient);
        }
        else
        {
            _api = new HuyaApiClient(httpClient);
        }

        _socketFactory = socketFactory;
    }

    public PlatformType Platform => PlatformType.Huya;

    public string RoomId => _roomId;

    public event Action<DanmakuMessage>? MessageReceived;

    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _connectState, 1) != 0)
        {
            throw new InvalidOperationException("虎牙弹幕客户端已连接或正在连接。");
        }

        _disconnectRequested = false;
        try
        {
            var snapshot = await _api.GetRoomAsync(_roomId, ct).ConfigureAwait(false);
            if (snapshot.PresenterUid <= 0)
            {
                throw new InvalidOperationException("未能解析出虎牙主播 uid，无法连接弹幕。");
            }

            if (!snapshot.IsLive)
            {
                throw new InvalidOperationException("虎牙直播间未开播。");
            }

            WebSocket socket;
            if (_socketFactory is not null)
            {
                socket = _socketFactory();
            }
            else
            {
                var client = new ClientWebSocket();
                var uri = new Uri(HuyaPacketParser.BuildSocketUrl(HuyaPacketParser.CreateGuid()));
                await client.ConnectAsync(uri, ct).ConfigureAwait(false);
                socket = client;
            }

            _socket = socket;
            _cts = new CancellationTokenSource();
            await SendAsync(HuyaPacketParser.BuildSubscribe((ulong)snapshot.PresenterUid), ct).ConfigureAwait(false);

            Interlocked.Exchange(ref _connectState, 2);
            var token = _cts.Token;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
        }
        catch
        {
            Interlocked.Exchange(ref _connectState, 0);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _disconnectRequested = true;
        Interlocked.Exchange(ref _connectState, 0);

        var cts = _cts;
        var socket = _socket;
        var loop = _receiveLoop;
        _cts = null;
        _socket = null;
        _receiveLoop = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 关闭失败直接 Dispose，不用向调用方冒错。
            }

            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
            }
        }

        cts?.Dispose();

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 接收循环可能因取消而异常结束，忽略。
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _api.Dispose();
        _ownedHttpClient?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var messageBuffer = new MemoryStream();
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket!.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = messageBuffer.ToArray();
                messageBuffer.SetLength(0);
                if (!HuyaPacketParser.TryParse(frame, out var messages))
                {
                    continue;
                }

                foreach (var message in messages)
                {
                    MessageReceived?.Invoke(new DanmakuMessage(
                        PlatformType.Huya,
                        _roomId,
                        message.SenderUid,
                        message.SenderName,
                        message.Content,
                        DateTimeOffset.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (!_disconnectRequested && !ct.IsCancellationRequested)
        {
            Disconnected?.Invoke(error);
        }
    }

    private async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket!.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
