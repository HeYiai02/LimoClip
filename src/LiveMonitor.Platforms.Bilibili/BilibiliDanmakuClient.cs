using System.Globalization;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站直播间实时弹幕客户端。
/// <para>
/// 连接流程：getDanmuInfo 拿 token/弹幕服务器地址 → 连 wss <c>/sub</c> → 发 op=7 认证包 →
/// 收到 op=8 且 code==0 才算连接成功 → 启动后台收包循环与 30s op=2 心跳。
/// </para>
/// <para>
/// 约定：<see cref="ConnectAsync"/> 只做一次连接尝试，失败直接抛异常，不做重连
/// （重连与 3s→10s→30s 退避由 DanmakuManager 统一负责）。主动 <see cref="DisconnectAsync"/>
/// 不触发 <see cref="Disconnected"/>；异常断开/对端正常关闭才触发（正常关闭参数为 null）。
/// </para>
/// </summary>
public sealed class BilibiliDanmakuClient : IDanmakuClient
{
    /// <summary>op=8 认证回执的等待上限（秒）。</summary>
    public const int HandshakeTimeoutSeconds = 10;

    /// <summary>op=2 心跳间隔。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private const string DanmuInfoUrl =
        "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";

    /// <summary>取 WBI 签名密钥（wbi_img）的 nav 接口；匿名请求 code=-101 时 data.wbi_img 仍然有效。</summary>
    private const string WbiNavUrl = "https://api.bilibili.com/x/web-interface/nav";

    /// <summary>getDanmuInfo 需要的 web_location 参数（与 B 站 Web 端一致）。</summary>
    private const string DanmuInfoWebLocation = "444.8";

    /// <summary>WBI 签名密钥缓存时长，过期后重新拉取 nav。</summary>
    private static readonly TimeSpan WbiKeyCacheLifetime = TimeSpan.FromHours(1);

    private const string BuvidSpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";

    /// <summary>
    /// 标记当前调用栈是否处于事件回调内。订阅方在事件回调里同步调用
    /// <see cref="DisconnectAsync"/>/<see cref="DisposeAsync"/> 时，不能等待当前正在执行的后台循环，否则自锁。
    /// </summary>
    private static readonly AsyncLocal<bool> InEventCallback = new();

    private readonly string _roomId;
    private readonly long _numericRoomId;
    private readonly string? _cookie;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<IBilibiliWebSocketConnection> _socketFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    private IBilibiliWebSocketConnection? _socket;
    private CancellationTokenSource? _lifeCts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private bool _connected;
    private bool _connectInProgress;
    private int _disposed;
    private volatile bool _disconnectRequested;
    private int _disconnectedRaised;
    private string? _wbiMixinKey;
    private DateTimeOffset _wbiMixinKeyExpiresAt;

    /// <param name="roomId">平台真实房间号（纯数字）。</param>
    /// <param name="cookie">完整 Cookie 头；未登录为 null。用于 buvid3 / DedeUserID。</param>
    /// <param name="httpClient">HTTP 客户端，测试注入；null 时自建 20s 超时实例。</param>
    /// <param name="socketFactory">WebSocket 连接工厂，测试注入；null 时用真实 ClientWebSocket。</param>
    /// <param name="delay">定时等待函数，测试注入；null 时用 Task.Delay。</param>
    /// <param name="clock">消息时间戳来源，测试注入；null 时用 DateTimeOffset.UtcNow。</param>
    public BilibiliDanmakuClient(
        string roomId,
        string? cookie,
        HttpClient? httpClient = null,
        Func<IBilibiliWebSocketConnection>? socketFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("B 站房间号不能为空。", nameof(roomId));
        }

        var trimmedRoomId = roomId.Trim();
        if (!long.TryParse(trimmedRoomId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericRoomId)
            || numericRoomId <= 0)
        {
            throw new ArgumentException($"B 站房间号必须为正整数：{roomId}", nameof(roomId));
        }

        _roomId = trimmedRoomId;
        _numericRoomId = numericRoomId;
        _cookie = PlatformDefaults.NormalizeCookie(cookie);
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient is null;
        _socketFactory = socketFactory ?? (static () => new ClientWebSocketBilibiliConnection());
        _delay = delay ?? (static (interval, ct) => Task.Delay(interval, ct));
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public PlatformType Platform => PlatformType.Bilibili;

    /// <inheritdoc />
    public string RoomId => _roomId;

    /// <inheritdoc />
    public event Action<DanmakuMessage>? MessageReceived;

    /// <inheritdoc />
    public event Action<Exception?>? Disconnected;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_connected)
            {
                throw new InvalidOperationException("B 站弹幕连接已建立，请先调用 DisconnectAsync。");
            }

            if (_connectInProgress)
            {
                throw new InvalidOperationException("B 站弹幕连接正在建立中。");
            }

            _connectInProgress = true;
        }

        IBilibiliWebSocketConnection? socket = null;
        try
        {
            // 兜底清理上一次异常断开后可能残留的 socket/后台任务。
            await DisconnectCoreAsync().ConfigureAwait(false);

            var server = await GetDanmuServerInfoAsync(ct).ConfigureAwait(false);
            var buvid = await ResolveBuvidAsync(ct).ConfigureAwait(false);
            var uid = ParseOwnerUid(_cookie);

            socket = _socketFactory();
            await socket.ConnectAsync(server.WebSocketUri, ct).ConfigureAwait(false);
            await socket
                .SendAsync(
                    BilibiliPacketParser.BuildAuthPacket(_numericRoomId, uid, server.Token, buvid),
                    ct)
                .ConfigureAwait(false);

            var pendingMessages = await WaitForAuthReplyAsync(socket, ct).ConfigureAwait(false);

            var connectedSocket = socket;
            socket = null;
            var lifeCts = new CancellationTokenSource();

            lock (_sync)
            {
                _socket = connectedSocket;
                _lifeCts = lifeCts;
                _connected = true;
                _disconnectRequested = false;
                _disconnectedRaised = 0;
            }

            // 认证回执同一帧里若夹带弹幕，先按序投递再启动后台循环。
            foreach (var message in pendingMessages)
            {
                RaiseMessageReceived(message);
            }

            var receiveTask = Task.Run(() => ReceiveLoopAsync(connectedSocket, lifeCts.Token));
            var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(connectedSocket, lifeCts.Token));
            lock (_sync)
            {
                _receiveTask = receiveTask;
                _heartbeatTask = heartbeatTask;
            }
        }
        catch
        {
            if (socket is not null)
            {
                try
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 连接失败路径上清理由本方法负责，socket 释放异常不再叠加抛出。
                }
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _connectInProgress = false;
            }
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return Task.CompletedTask;
        }

        return DisconnectCoreAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsHttpClient)
            {
                _http.Dispose();
            }
        }
    }

    private async Task DisconnectCoreAsync()
    {
        IBilibiliWebSocketConnection? socket;
        CancellationTokenSource? lifeCts;
        Task? receiveTask;
        Task? heartbeatTask;

        lock (_sync)
        {
            _disconnectRequested = true;
            socket = _socket;
            _socket = null;
            lifeCts = _lifeCts;
            _lifeCts = null;
            receiveTask = _receiveTask;
            _receiveTask = null;
            heartbeatTask = _heartbeatTask;
            _heartbeatTask = null;
            _connected = false;
        }

        if (socket is null && lifeCts is null)
        {
            return;
        }

        if (lifeCts is not null)
        {
            try
            {
                lifeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放则无需再取消。
            }
        }

        try
        {
            socket?.Abort();
        }
        catch (Exception)
        {
            // Abort 失败不影响后续释放。
        }

        // 若当前正处于事件回调栈里（订阅方在事件里同步 Disconnect/Dispose），
        // 等待后台循环会等待自己，跳过等待；循环已捕获全部异常，不会被观察为未处理异常。
        if (!InEventCallback.Value)
        {
            await AwaitQuietlyAsync(receiveTask).ConfigureAwait(false);
            await AwaitQuietlyAsync(heartbeatTask).ConfigureAwait(false);
        }

        if (socket is not null)
        {
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放异常不外抛，DisconnectAsync/DisposeAsync 保证幂等且不炸。
            }
        }

        lifeCts?.Dispose();
    }

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台循环内部已兜底，这里只保证不把未观察异常抛给断线路径。
        }
    }

    private async Task ReceiveLoopAsync(IBilibiliWebSocketConnection socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null)
                {
                    // 对端正常关闭（Close 帧）：Disconnected 参数为 null。
                    SignalDisconnected(null);
                    return;
                }

                var result = BilibiliPacketParser.ParseFrame(frame, _roomId, _clock);
                foreach (var message in result.Messages)
                {
                    RaiseMessageReceived(message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 主动断开/释放：不触发 Disconnected。
        }
        catch (Exception ex)
        {
            SignalDisconnected(ex);
        }
    }

    private async Task HeartbeatLoopAsync(IBilibiliWebSocketConnection socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _delay(HeartbeatInterval, ct).ConfigureAwait(false);
                await socket.SendAsync(BilibiliPacketParser.BuildHeartbeatPacket(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 主动断开/释放：不触发 Disconnected。
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                SignalDisconnected(ex);
            }
        }
    }

    /// <summary>连接异常断开：只投递一次 Disconnected，并停掉心跳、中止 socket。</summary>
    private void SignalDisconnected(Exception? error)
    {
        if (_disconnectRequested || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 1)
        {
            return;
        }

        IBilibiliWebSocketConnection? socket;
        CancellationTokenSource? lifeCts;
        lock (_sync)
        {
            _connected = false;
            socket = _socket;
            lifeCts = _lifeCts;
        }

        try
        {
            socket?.Abort();
        }
        catch (Exception)
        {
            // Abort 失败不影响断线通知。
        }

        if (lifeCts is not null)
        {
            try
            {
                lifeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放则无需再取消。
            }
        }

        RaiseDisconnected(error);
    }

    private async Task<List<DanmakuMessage>> WaitForAuthReplyAsync(
        IBilibiliWebSocketConnection socket,
        CancellationToken ct)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        var pendingMessages = new List<DanmakuMessage>();

        try
        {
            while (true)
            {
                var frame = await socket.ReceiveAsync(handshakeCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    throw new InvalidOperationException("B 站弹幕服务器在认证完成前关闭了连接。");
                }

                var result = BilibiliPacketParser.ParseFrame(frame, _roomId, _clock);
                if (result.Messages.Count > 0)
                {
                    pendingMessages.AddRange(result.Messages);
                }

                if (!result.AuthReplyReceived)
                {
                    continue;
                }

                if (result.AuthReplyCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(result.AuthReplyMessage)
                        ? string.Empty
                        : $" {result.AuthReplyMessage}";
                    throw new InvalidOperationException(
                        $"B 站弹幕服务器认证失败：code={result.AuthReplyCode}{message}");
                }

                return pendingMessages;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"B 站弹幕服务器认证超时（约 {HandshakeTimeoutSeconds} 秒）。");
        }
    }

    private async Task<DanmuServerInfo> GetDanmuServerInfoAsync(CancellationToken ct)
    {
        // getDanmuInfo 已加风控：缺少 WBI 签名会返回 code=-352，这里始终签名后再请求。
        var mixinKey = await GetWbiMixinKeyAsync(ct).ConfigureAwait(false);
        var signedQuery = BilibiliWbiSigner.Sign(
            new[]
            {
                new KeyValuePair<string, string>("id", _roomId),
                new KeyValuePair<string, string>("type", "0"),
                new KeyValuePair<string, string>("web_location", DanmuInfoWebLocation),
            },
            mixinKey,
            _clock().ToUnixTimeSeconds());
        var url = string.Concat(DanmuInfoUrl, "?", signedQuery);

        using var document = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var code = JsonRead.Int(root, "code");
        if (code != 0)
        {
            var message = JsonRead.String(root, "message") ?? string.Empty;
            throw new InvalidOperationException(
                $"B 站弹幕服务器信息获取失败：code={code} {message}".Trim());
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("B 站弹幕服务器信息缺少 data 字段。");
        }

        var token = JsonRead.String(data, "token") ?? string.Empty;
        if (token.Length == 0)
        {
            throw new InvalidOperationException("B 站弹幕服务器信息缺少 token 字段。");
        }

        if (!data.TryGetProperty("host_list", out var hostList) || hostList.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("B 站弹幕服务器信息缺少 host_list 字段。");
        }

        var uri = SelectHostUri(hostList)
            ?? throw new InvalidOperationException("B 站弹幕服务器信息未返回可用的 host。");
        return new DanmuServerInfo(uri, token);
    }

    private async Task<string> GetWbiMixinKeyAsync(CancellationToken ct)
    {
        var now = _clock();
        lock (_sync)
        {
            if (_wbiMixinKey is not null && now < _wbiMixinKeyExpiresAt)
            {
                return _wbiMixinKey;
            }
        }

        using var document = await GetJsonAsync(WbiNavUrl, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("wbi_img", out var wbiImg)
            || wbiImg.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"B 站 WBI 签名初始化失败：nav 接口未返回签名密钥（code={JsonRead.Int(root, "code")}）。");
        }

        var imgKey = ExtractWbiKey(JsonRead.String(wbiImg, "img_url"));
        var subKey = ExtractWbiKey(JsonRead.String(wbiImg, "sub_url"));
        if (imgKey is null || subKey is null)
        {
            throw new InvalidOperationException("B 站 WBI 签名初始化失败：nav 接口返回的 wbi_img 不完整。");
        }

        var mixinKey = BilibiliWbiSigner.CreateMixinKey(imgKey, subKey);
        lock (_sync)
        {
            _wbiMixinKey = mixinKey;
            _wbiMixinKeyExpiresAt = now + WbiKeyCacheLifetime;
        }

        return mixinKey;
    }

    /// <summary>从 wbi_img 的图片 URL 里取文件名 key（如 .../7cd0...c.png → 7cd0...c）。</summary>
    private static string? ExtractWbiKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        var slash = trimmed.LastIndexOf('/');
        var fileName = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        var dot = fileName.IndexOf('.');
        if (dot > 0)
        {
            fileName = fileName[..dot];
        }

        return fileName.Length == 0 ? null : fileName;
    }

    private async Task<string> ResolveBuvidAsync(CancellationToken ct)
    {
        var cookieBuvid = GetCookieValue(_cookie, "buvid3");
        if (!string.IsNullOrEmpty(cookieBuvid))
        {
            return cookieBuvid;
        }

        // 游客态取 buvid3；接口失败降级为空串，不阻断弹幕连接（代价是无法拿到真实 uid）。
        try
        {
            using var document = await GetJsonAsync(BuvidSpiUrl, ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (JsonRead.Int(root, "code") != 0)
            {
                return string.Empty;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            return JsonRead.String(data, "b_3") ?? JsonRead.String(data, "buvid3") ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", $"{PlatformDefaults.BilibiliLiveBase}/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        if (url.StartsWith("https://api.live.bilibili.com", StringComparison.Ordinal))
        {
            // 直播接口的风控会校验来源页；与 Web 端保持一致。
            request.Headers.TryAddWithoutValidation("Origin", PlatformDefaults.BilibiliLiveBase);
        }
        if (_cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"B 站接口 HTTP {(int)response.StatusCode}：{PlatformDefaults.TrimForError(body)}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"B 站接口返回了无法解析的 JSON：{PlatformDefaults.TrimForError(body)}", ex);
        }
    }

    private static Uri? SelectHostUri(JsonElement hostList)
    {
        var candidates = new List<HostCandidate>();
        foreach (var entry in hostList.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var host = JsonRead.String(entry, "host");
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            candidates.Add(new HostCandidate(
                host.Trim(),
                JsonRead.Int(entry, "wss_port"),
                JsonRead.Int(entry, "ws_port"),
                JsonRead.Int(entry, "port")));
        }

        foreach (var candidate in candidates)
        {
            if (candidate.WssPort > 0 && TryBuildWebSocketUri(candidate.Host, candidate.WssPort, out var uri))
            {
                return uri;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.WsPort > 0 && TryBuildWebSocketUri(candidate.Host, candidate.WsPort, out var uri))
            {
                return uri;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Port > 0 && TryBuildWebSocketUri(candidate.Host, candidate.Port, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static bool TryBuildWebSocketUri(string host, int port, out Uri? uri)
    {
        var normalized = host.Trim();
        if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[6..];
        }
        else if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[8..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        normalized = normalized.TrimEnd('/');
        var url = $"wss://{normalized}:{port.ToString(CultureInfo.InvariantCulture)}/sub";
        return Uri.TryCreate(url, UriKind.Absolute, out uri);
    }

    private static string? GetCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        foreach (var pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = pair.Trim();
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!segment.AsSpan(0, separator).Trim().Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            var value = segment[(separator + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static long ParseOwnerUid(string? cookie)
    {
        var raw = GetCookieValue(cookie, "DedeUserID");
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid > 0
            ? uid
            : 0;
    }

    private void RaiseMessageReceived(DanmakuMessage message)
    {
        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        InEventCallback.Value = true;
        try
        {
            handler(message);
        }
        catch (Exception)
        {
            // 订阅方异常不能拖垮后台收包循环。
        }
        finally
        {
            InEventCallback.Value = false;
        }
    }

    private void RaiseDisconnected(Exception? error)
    {
        var handler = Disconnected;
        if (handler is null)
        {
            return;
        }

        InEventCallback.Value = true;
        try
        {
            handler(error);
        }
        catch (Exception)
        {
            // 订阅方异常不能拖垮后台循环。
        }
        finally
        {
            InEventCallback.Value = false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(BilibiliDanmakuClient));
        }
    }

    private readonly record struct DanmuServerInfo(Uri WebSocketUri, string Token);

    private readonly record struct HostCandidate(string Host, int WssPort, int WsPort, int Port);
}
