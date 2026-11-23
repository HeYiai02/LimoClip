using System.Net.Sockets;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>
/// 斗鱼弹幕客户端。使用斗鱼开放 TCP 弹幕服务器（danmuproxy.douyu.com:8601）的 STT 协议：
/// 登录（loginreq）→ 收到 loginres 后加组（joingroup，gid=-9999 海量弹幕模式）→ 循环收弹幕；
/// 收到服务端 pingreq 或每 45 秒发送一次 mrkl 心跳。
/// </summary>
public sealed class DouyuDanmakuClient : IDanmakuClient
{
    private const string DanmakuHost = "danmuproxy.douyu.com";
    private const int DanmakuPort = 8601;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan JoinGroupTimeout = TimeSpan.FromSeconds(10);

    private readonly string _roomId;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private int _state;
    private volatile bool _disconnectRequested;
    private bool _disposed;

    public DouyuDanmakuClient(string roomId, string? authCookie = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("斗鱼房间号为空。", nameof(roomId));
        }

        var normalized = roomId.Trim();
        if (!DouyuUrlParser.IsRoomNumber(normalized))
        {
            normalized = DouyuUrlParser.ExtractRoomId(normalized);
        }

        if (!string.IsNullOrEmpty(authCookie) && (authCookie.Contains('\r') || authCookie.Contains('\n')))
        {
            throw new ArgumentException("斗鱼 Cookie 不能包含换行符。", nameof(authCookie));
        }

        // 斗鱼弹幕匿名可用，Cookie 暂不参与握手；保留参数以匹配插件契约。
        _ = authCookie;
        _roomId = normalized;
    }

    public PlatformType Platform => PlatformType.Douyu;

    public string RoomId => _roomId;

    public event Action<DanmakuMessage>? MessageReceived;

    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {

        if (Interlocked.Exchange(ref _state, 1) != 0)
        {
            throw new InvalidOperationException("斗鱼弹幕客户端已连接或正在连接中。");
        }

        try
        {
            var tcpClient = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await tcpClient.ConnectAsync(DanmakuHost, DanmakuPort, timeoutCts.Token);
            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();

            await SendAsync($"type@=loginreq/roomid@={DouyuStt.Escape(_roomId)}/\0", cancellationToken);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            _receiveLoop = RunReceiveLoopAsync(linked.Token);
            _heartbeatLoop = RunHeartbeatLoopAsync(linked.Token);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _state, 0);
            _tcpClient?.Dispose();
            _tcpClient = null;
            _stream = null;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _disconnectRequested = true;
        if (Interlocked.Exchange(ref _state, 2) == 2)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        TryCloseSocket();

        var loops = new[] { _receiveLoop, _heartbeatLoop };
        try
        {
            await Task.WhenAll(loops.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
            _sendLock.Dispose();
            try
            {
                _stream?.Dispose();
            }
            catch (Exception)
            {
            }

            _tcpClient?.Dispose();
            _tcpClient = null;
            _stream = null;
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("斗鱼弹幕连接未建立。");
            var header = new byte[4];
            var joined = false;

            while (!ct.IsCancellationRequested)
            {
                var read = await ReadExactAsync(stream, header, ct);
                if (!read)
                {
                    break;
                }

                var declared = BitConverter.ToInt32(header, 0);
                if (declared <= 8 || declared > 1_000_000)
                {
                    throw new InvalidDataException($"斗鱼弹幕报文长度异常：{declared}");
                }

                var rest = new byte[declared];
                var got = 0;
                while (got < declared)
                {
                    var n = await stream.ReadAsync(rest.AsMemory(got, declared - got), ct);
                    if (n <= 0)
                    {
                        throw new IOException("斗鱼弹幕连接已断开。");
                    }

                    got += n;
                }

                var packet = new byte[declared + 4];
                header.CopyTo(packet, 0);
                rest.CopyTo(packet, 4);
                if (!DouyuStt.TryParsePacket(packet, packet.Length, out var type, out var payload))
                {
                    continue;
                }

                _ = type;
                var stt = DouyuStt.ParseStt(payload);
                if (!stt.TryGetValue("type", out var messageType))
                {
                    continue;
                }

                switch (messageType)
                {
                    case "loginres" when !joined:
                        joined = true;
                        await SendAsync($"type@=joingroup/rid@={DouyuStt.Escape(_roomId)}/gid@=-9999/\0", ct);
                        break;
                    case "pingreq":
                        await SendAsync("type@=mrkl/\0", ct);
                        break;
                    case "chatmsg":
                        if (stt.TryGetValue("txt", out var text) && !string.IsNullOrWhiteSpace(text))
                        {
                            var uid = stt.TryGetValue("uid", out var uidText) ? uidText : string.Empty;
                            var nick = stt.TryGetValue("nn", out var nickText) ? nickText : "未知用户";
                            RaiseMessage(uid, nick, text, false);
                        }

                        break;
                    case "dgb":
                        if (stt.TryGetValue("gfcnt", out _) || stt.TryGetValue("gfid", out _))
                        {
                            var uid = stt.TryGetValue("uid", out var giftUid) ? giftUid : string.Empty;
                            var nick = stt.TryGetValue("nn", out var giftNick) ? giftNick : "未知用户";
                            var name = stt.TryGetValue("gfid", out var giftId) ? "赠送礼物 #" + giftId : "赠送礼物";
                            RaiseMessage(uid, nick, name, true);
                        }

                        break;
                }

                if (!joined)
                {
                    // 等 loginres；10 秒未收到说明服务器拒绝或协议变化。
                    if (_joinDeadline is { } deadline && DateTimeOffset.UtcNow > deadline)
                    {
                        throw new InvalidOperationException("斗鱼弹幕登录超时（未收到 loginres）。");
                    }
                }
                else
                {
                    _joinDeadline = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!_disconnectRequested)
            {
                RaiseDisconnected(ex);
            }
        }
    }

    private DateTimeOffset? _joinDeadline = DateTimeOffset.UtcNow.Add(JoinGroupTimeout);

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await SendAsync("type@=mrkl/\0", ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disconnectRequested)
            {
                RaiseDisconnected(ex);
            }
        }
    }

    private void RaiseMessage(string uid, string nick, string content, bool isGift)
    {
        var message = new DanmakuMessage(
            Platform: PlatformType.Douyu,
            RoomId: _roomId,
            SenderUid: uid,
            SenderName: nick,
            Content: content,
            Timestamp: DateTimeOffset.UtcNow,
            IsGiftOrSuperChat: isGift);
        MessageReceived?.Invoke(message);
    }

    private void RaiseDisconnected(Exception? error) => Disconnected?.Invoke(error);

    private async Task SendAsync(string payload, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null)
        {
            throw new InvalidOperationException("斗鱼弹幕连接尚未建立。");
        }

        var packet = DouyuStt.BuildPacket(payload);
        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(packet, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n <= 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }

    private void TryCloseSocket()
    {
        try
        {
            _stream?.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            _tcpClient?.Close();
        }
        catch (Exception)
        {
        }
    }
}


