using System.Net.WebSockets;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// 抖音直播弹幕客户端：web_rid → enter 接口取真实 room_id 与 ttwid →
/// 拼签名 WebSocket 地址（sign.js 通过 Jint 计算 signature）→ 接收 PushFrame → gzip 解压 → Protobuf 解析 → 回 Ack。
/// 抖音风控较强，失败时统一抛出可操作的中文错误（未开播 / 签名脚本缺失 / 校验未通过）。
/// </summary>
public sealed class DouyinDanmakuClient : IDanmakuClient
{
    private const string WebSocketHost = "wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/";
    private const string WebSocketUserAgent = DouyinApiClient.UserAgent;
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly DouyinApiClient _api;
    private readonly DouyinSigner _signer;
    private readonly bool _ownsApi;
    private readonly bool _ownsSigner;
    private readonly string? _authCookie;
    private readonly Func<WebSocket>? _socketFactory;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;
    private int _state; // 0=idle 1=connecting/connected 2=disposed
    private bool _disconnectRequested;
    private string? _resolvedRoomId;
    private string? _ttwid;
    private Task? _receiveTask;
    private Task? _heartbeatTask;

    public DouyinDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null,
        DouyinApiClient? api = null,
        DouyinSigner? signer = null,
        Func<WebSocket>? socketFactory = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("抖音直播间号不能为空。", nameof(roomId));
        if (authCookie is not null && (authCookie.Contains('\r') || authCookie.Contains('\n')))
            throw new ArgumentException("Cookie 不能包含换行符。", nameof(authCookie));

        RoomId = DouyinUrlParser.TryExtractRoomId(roomId, out var extracted) ? extracted : roomId.Trim();
        if (!DouyinUrlParser.IsRoomNumber(RoomId))
            throw new ArgumentException($"无法识别的抖音直播间号：{roomId}", nameof(roomId));

        _authCookie = string.IsNullOrWhiteSpace(authCookie) ? null : authCookie;
        _socketFactory = socketFactory;
        _api = api ?? new DouyinApiClient(httpClient);
        _ownsApi = api is null;
        _signer = signer ?? new DouyinSigner(httpClient);
        _ownsSigner = signer is null;
    }

    public PlatformType Platform => PlatformType.Douyin;

    public string RoomId { get; }

    public event Action<DanmakuMessage>? MessageReceived;

    public event Action<Exception?>? Disconnected;

    /// <summary>已连接后的真实房间号（webcast room_id），用于诊断。</summary>
    public string? ResolvedRoomId => _resolvedRoomId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("抖音弹幕连接已经建立或正在建立。");

        try
        {
            var snapshot = await _api.GetRoomAsync(RoomId, _authCookie, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsLive)
                throw new InvalidOperationException(
                    $"抖音直播间 {RoomId} 当前未开播，无法连接弹幕。");
            _resolvedRoomId = snapshot.RoomId;
            _ttwid = snapshot.Ttwid;

            await _signer.PrepareAsync(cancellationToken).ConfigureAwait(false);

            var userUniqueId = GenerateRandomDigits(16);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = BuildWebSocketUrl(snapshot.RoomId, userUniqueId, now);
            var signature = _signer.Sign(url);
            url += "&signature=" + Uri.EscapeDataString(signature);

            var socket = _socketFactory?.Invoke() ?? new ClientWebSocket();
            if (socket is ClientWebSocket clientSocket)
            {
                clientSocket.Options.SetRequestHeader("User-Agent", WebSocketUserAgent);
                clientSocket.Options.SetRequestHeader("Referer", "https://live.douyin.com/");
                clientSocket.Options.SetRequestHeader("Origin", "https://live.douyin.com");
                clientSocket.Options.SetRequestHeader("Cookie", BuildCookieHeader());

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await clientSocket.ConnectAsync(new Uri(url), connectTimeout.Token).ConfigureAwait(false);
            }

            _socket = socket;

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_lifetime.Token));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _state, 0);
            throw new InvalidOperationException(BuildConnectErrorMessage(ex), ex);
        }
    }

    public async Task DisconnectAsync()
    {
        _disconnectRequested = true;
        _lifetime.Cancel();

        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 主动断开时忽略关闭错误。
            }

            try { socket.Dispose(); } catch { }
        }

        await WaitLoopsAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _state, 2);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _sendLock.Dispose();
        if (_ownsSigner) _signer.Dispose();
        if (_ownsApi) _api.Dispose();
    }

    /// <summary>按网页版格式拼 WebSocket 地址（未追加 signature）。</summary>
    internal static string BuildWebSocketUrl(string roomId, string userUniqueId, long nowMs)
    {
        var wrdsVersion = GenerateRandomDigits(19);
        var fetchRoomId = GenerateRandomDigits(19);
        var url = new StringBuilder(WebSocketHost);
        url.Append("?app_name=douyin_web&version_code=180800")
            .Append("&webcast_sdk_version=1.0.14-beta.0&update_version_code=1.0.14-beta.0&compress=gzip")
            .Append("&device_platform=web&cookie_enabled=true&screen_width=1536&screen_height=864")
            .Append("&browser_language=zh-CN&browser_platform=Win32&browser_name=Mozilla")
            .Append("&browser_version=").Append(Uri.EscapeDataString(
                "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"))
            .Append("&browser_online=true&tz_name=Asia/Shanghai")
            .Append("&cursor=d-1_u-1_fh-").Append(fetchRoomId).Append("_t-").Append(nowMs).Append("_r-1")
            .Append("&internal_ext=internal_src:dim|wss_push_room_id:").Append(roomId)
            .Append("|wss_push_did:").Append(userUniqueId)
            .Append("|first_req_ms:").Append(nowMs)
            .Append("|fetch_time:").Append(nowMs)
            .Append("|seq:1|wss_info:0-").Append(nowMs).Append("-0-0")
            .Append("|wrds_v:").Append(wrdsVersion)
            .Append("&host=https://live.douyin.com&aid=6383&live_id=1&did_rule=3&endpoint=live_pc&support_wrds=1")
            .Append("&user_unique_id=").Append(userUniqueId)
            .Append("&im_path=/webcast/im/fetch/&identity=audience")
            .Append("&need_persist_msg_count=15&insert_task_id=&live_reason=&room_id=").Append(roomId)
            .Append("&heartbeatDuration=0");
        return url.ToString();
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var heartbeat = DouyinProto.BuildPushFrame("hb", Array.Empty<byte>(), 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendAsync(heartbeat, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var accumulator = new MemoryStream();
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _socket!.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    failure = new InvalidOperationException(
                        $"抖音弹幕服务端关闭了连接（{(int?)result.CloseStatus} {result.CloseStatusDescription}）。");
                    break;
                }

                accumulator.Write(buffer, 0, result.Count);
                if (accumulator.Length > MaxFrameBytes)
                {
                    failure = new InvalidOperationException("抖音弹幕数据帧超过大小限制，已断开。");
                    break;
                }

                if (!result.EndOfMessage) continue;

                var frameBytes = accumulator.ToArray();
                accumulator.SetLength(0);
                await HandleFrameAsync(frameBytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动断开。
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!_disconnectRequested)
            Disconnected?.Invoke(failure);
    }

    private async Task HandleFrameAsync(byte[] frameBytes, CancellationToken ct)
    {
        var frame = DouyinProto.ParsePushFrame(frameBytes);
        if (frame.PayloadType is "hb" or "ack") return;
        if (frame.Payload.Length == 0) return;

        byte[] responseBytes;
        try
        {
            responseBytes = DouyinProto.Decompress(frame.Payload);
        }
        catch
        {
            return;
        }

        var response = DouyinProto.ParseResponse(responseBytes);
        if (response.NeedAck)
        {
            var ack = DouyinProto.BuildPushFrame("ack", Encoding.UTF8.GetBytes(response.InternalExt), frame.LogId);
            await SendAsync(ack, ct).ConfigureAwait(false);
        }

        foreach (var message in response.Messages)
        {
            var chat = DouyinProto.ParseChatMessage(message.Method, message.Payload);
            if (chat is null || string.IsNullOrWhiteSpace(chat.Content)) continue;
            MessageReceived?.Invoke(new DanmakuMessage(
                Platform: PlatformType.Douyin,
                RoomId: RoomId,
                SenderUid: chat.UserId.ToString(),
                SenderName: chat.NickName,
                Content: chat.Content,
                Timestamp: DateTimeOffset.UtcNow,
                IsGiftOrSuperChat: chat.IsGift));
        }
    }

    private async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private string BuildCookieHeader() => DouyinApiClient.BuildCookieHeader(_authCookie, _ttwid ?? string.Empty);

    private async Task WaitLoopsAsync()
    {
        var loops = new[] { _receiveTask, _heartbeatTask }.Where(t => t is not null).Cast<Task>().ToArray();
        if (loops.Length == 0) return;
        try
        {
            await Task.WhenAny(Task.WhenAll(loops), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        catch
        {
            // 忽略等待超时。
        }
    }

    private static string BuildConnectErrorMessage(Exception exception) => exception switch
    {
        InvalidOperationException when exception.Message.Contains("签名脚本", StringComparison.Ordinal) =>
            exception.Message,
        InvalidOperationException => exception.Message,
        WebSocketException => "抖音弹幕连接失败（可能因签名/风控校验未通过）：" + exception.Message +
                              "。可稍后重试；如持续失败请先在内置浏览器打开一次抖音直播间。",
        _ => "抖音弹幕连接失败：" + exception.Message,
    };

    private static string GenerateRandomDigits(int length)
    {
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            builder.Append((char)('0' + Random.Shared.Next(10)));
        if (builder[0] == '0') builder[0] = (char)('1' + Random.Shared.Next(9));
        return builder.ToString();
    }
}
