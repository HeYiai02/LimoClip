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
    internal const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly DouyinApiClient _api;
    private readonly DouyinSigner _signer;
    private readonly bool _ownsApi;
    private readonly bool _ownsSigner;
    private readonly string? _authCookie;
    private readonly Func<IDouyinWebSocketConnection> _connectionFactory;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IDouyinWebSocketConnection? _connection;
    private int _state; // 0=idle 1=connecting/connected 2=disposed
    private volatile bool _disconnectRequested;
    private int _disconnectedRaised;
    private string? _resolvedRoomId;
    private string? _ttwid;
    private Task? _receiveTask;
    private Task? _heartbeatTask;

    public DouyinDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null,
        DouyinApiClient? api = null,
        DouyinSigner? signer = null)
        : this(roomId, authCookie, httpClient, api, signer, connectionFactory: null)
    {
    }

    /// <param name="connectionFactory">WebSocket 连接工厂，测试注入；null 时使用真实 ClientWebSocket。</param>
    internal DouyinDanmakuClient(
        string roomId,
        string? authCookie,
        HttpClient? httpClient,
        DouyinApiClient? api,
        DouyinSigner? signer,
        Func<IDouyinWebSocketConnection>? connectionFactory)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("抖音直播间号不能为空。", nameof(roomId));
        if (authCookie is not null && (authCookie.Contains('\r') || authCookie.Contains('\n')))
            throw new ArgumentException("Cookie 不能包含换行符。", nameof(authCookie));

        RoomId = DouyinUrlParser.TryExtractRoomId(roomId, out var extracted) ? extracted : roomId.Trim();
        if (!DouyinUrlParser.IsRoomNumber(RoomId))
            throw new ArgumentException($"无法识别的抖音直播间号：{roomId}", nameof(roomId));

        _authCookie = string.IsNullOrWhiteSpace(authCookie) ? null : authCookie;
        _connectionFactory = connectionFactory ?? (static () => new ClientWebSocketDouyinConnection(MaxFrameBytes));
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

        IDouyinWebSocketConnection? connection = null;
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

            connection = _connectionFactory();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = WebSocketUserAgent,
                ["Referer"] = "https://live.douyin.com/",
                ["Origin"] = "https://live.douyin.com",
                ["Cookie"] = BuildCookieHeader(),
            };

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await connection.ConnectAsync(new Uri(url), headers, connectTimeout.Token).ConfigureAwait(false);

            _connection = connection;
            connection = null; // 所有权已交给实例，失败路径不再释放。

            _disconnectRequested = false;
            Interlocked.Exchange(ref _disconnectedRaised, 0);

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_lifetime.Token));
        }
        catch (Exception ex)
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 连接失败路径上清理由本方法负责，释放异常不再叠加抛出。
                }
            }

            Interlocked.Exchange(ref _state, 0);
            throw new InvalidOperationException(BuildConnectErrorMessage(ex), ex);
        }
    }

    public async Task DisconnectAsync()
    {
        _disconnectRequested = true;
        _lifetime.Cancel();

        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 主动断开时忽略关闭错误。
            }
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
            catch (Exception ex)
            {
                // 心跳发送失败说明连接已不可用：主动触发一次断线通知（与接收循环共享单次触发保护），
                // 让上层 DanmakuManager 进入退避重连，而不是静默退出后永不重连。
                NotifyDisconnected(ex);
                AbortConnection();
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frameBytes = await _connection!.ReceiveMessageAsync(ct).ConfigureAwait(false);
                if (frameBytes is null)
                {
                    failure = new InvalidOperationException("抖音弹幕服务端关闭了连接。");
                    break;
                }

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
        finally
        {
            NotifyDisconnected(failure);
        }
    }

    /// <summary>解析并分发一帧数据（internal 供单测覆盖 gzip/回执等帧处理）。</summary>
    internal async Task HandleFrameAsync(byte[] frameBytes, CancellationToken ct)
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
        var connection = _connection;
        if (connection is null || connection.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>断线通知只触发一次：接收循环与心跳循环可能同时因同一故障退出。</summary>
    private void NotifyDisconnected(Exception? failure)
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0) return;
        if (_disconnectRequested) return;
        Disconnected?.Invoke(failure);
    }

    /// <summary>中止连接并取消后台循环（由心跳失败路径触发，促使接收循环尽快退出）。</summary>
    private void AbortConnection()
    {
        try
        {
            _connection?.Abort();
        }
        catch
        {
            // 中止异常无需向上传播。
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 已取消/已释放时忽略。
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
