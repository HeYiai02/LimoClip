using System.Globalization;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 直播间匿名只读弹幕客户端（IRC over WebSocket）。
/// <para>
/// 连接流程：连 <c>wss://irc-ws.chat.twitch.tv:443</c> → 依次发
/// <c>CAP REQ :twitch.tv/tags twitch.tv/commands</c>、<c>PASS SCHMOOOPIIE</c>、
/// <c>NICK justinfan#####</c>、<c>JOIN #频道</c> → 启动后台收包循环。
/// 匿名连接不需要 OAuth；<paramref name="cookie"/> 只为与平台客户端构造签名保持一致，当前不参与握手。
/// </para>
/// <para>
/// 约定：<see cref="ConnectAsync"/> 只做一次连接尝试，失败直接抛异常，不做重连
/// （重连与 3s→10s→30s 退避由 DanmakuManager 统一负责）。主动 <see cref="DisconnectAsync"/>
/// 不触发 <see cref="Disconnected"/>；异常断开/对端正常关闭/RECONNECT 才触发（正常关闭参数为 null）。
/// </para>
/// </summary>
public sealed class TwitchDanmakuClient : IDanmakuClient
{
    private const string LineEnding = "\r\n";
    private const string CapRequestLine = "CAP REQ :twitch.tv/tags twitch.tv/commands";
    private const string AnonymousPassLine = "PASS SCHMOOOPIIE";
    private const string AnonymousNickPrefix = "justinfan";
    private const string JoinPrefix = "JOIN #";
    private const string PongLine = "PONG :tmi.twitch.tv";
    private const string QuitLine = "QUIT :bye";

    private static readonly Uri IrcUri = new("wss://irc-ws.chat.twitch.tv:443");

    /// <summary>
    /// 标记当前调用栈是否处于事件回调内。订阅方在事件回调里同步调用
    /// <see cref="DisconnectAsync"/>/<see cref="DisposeAsync"/> 时，不能等待当前正在执行的后台循环，否则自锁。
    /// </summary>
    private static readonly AsyncLocal<bool> InEventCallback = new();

    private readonly string _roomId;
    private readonly Func<ITwitchIrcConnection> _connectionFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    private ITwitchIrcConnection? _connection;
    private CancellationTokenSource? _lifeCts;
    private Task? _receiveTask;
    private bool _connected;
    private bool _connectInProgress;
    private int _disposed;
    private volatile bool _disconnectRequested;
    private int _disconnectedRaised;

    /// <param name="roomId">Twitch 频道 login（大小写不敏感，内部统一转小写并去掉前导 #）。</param>
    /// <param name="cookie">完整 Cookie 头；匿名只读模式不参与握手，仅做输入校验。</param>
    /// <param name="connectionFactory">WebSocket 连接工厂，测试注入；null 时用真实 ClientWebSocket。</param>
    /// <param name="delay">
    /// 定时等待函数，测试注入。当前 IRC 协议无需客户端定时任务（服务端 PING 由收包循环即时应答），
    /// 参数为与 B 站客户端同构/后续认证扩展保留。
    /// </param>
    /// <param name="clock">消息时间戳来源，测试注入；null 时用 DateTimeOffset.UtcNow。</param>
    public TwitchDanmakuClient(
        string roomId,
        string? cookie,
        Func<ITwitchIrcConnection>? connectionFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("Twitch 频道名不能为空。", nameof(roomId));
        }

        var channel = roomId.Trim();
        if (channel.StartsWith('#'))
        {
            channel = channel[1..];
        }

        channel = channel.ToLowerInvariant();
        if (channel.Length == 0)
        {
            throw new ArgumentException($"无法识别的 Twitch 频道名：{roomId}", nameof(roomId));
        }

        _roomId = channel;
        _ = PlatformDefaults.NormalizeCookie(cookie);
        _ = delay;
        _connectionFactory = connectionFactory ?? (static () => new ClientWebSocketTwitchConnection());
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public PlatformType Platform => PlatformType.Twitch;

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
                throw new InvalidOperationException("Twitch 弹幕连接已建立，请先调用 DisconnectAsync。");
            }

            if (_connectInProgress)
            {
                throw new InvalidOperationException("Twitch 弹幕连接正在建立中。");
            }

            _connectInProgress = true;
        }

        ITwitchIrcConnection? connection = null;
        try
        {
            // 兜底清理上一次异常断开后可能残留的连接/后台任务。
            await DisconnectCoreAsync().ConfigureAwait(false);

            connection = _connectionFactory();
            await connection.ConnectAsync(IrcUri, ct).ConfigureAwait(false);

            var nick = AnonymousNickPrefix
                + Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);
            await connection.SendAsync(CapRequestLine + LineEnding, ct).ConfigureAwait(false);
            await connection.SendAsync(AnonymousPassLine + LineEnding, ct).ConfigureAwait(false);
            await connection.SendAsync("NICK " + nick + LineEnding, ct).ConfigureAwait(false);
            await connection.SendAsync(JoinPrefix + _roomId + LineEnding, ct).ConfigureAwait(false);

            var lifeCts = new CancellationTokenSource();
            var connectedConnection = connection;
            connection = null;

            lock (_sync)
            {
                _connection = connectedConnection;
                _lifeCts = lifeCts;
                _connected = true;
                _disconnectRequested = false;
                _disconnectedRaised = 0;
            }

            var receiveTask = Task.Run(() => ReceiveLoopAsync(connectedConnection, lifeCts.Token));
            lock (_sync)
            {
                _receiveTask = receiveTask;
            }
        }
        catch
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 连接失败路径上清理由本方法负责，连接释放异常不再叠加抛出。
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

        await DisconnectCoreAsync().ConfigureAwait(false);
    }

    private async Task DisconnectCoreAsync()
    {
        ITwitchIrcConnection? connection;
        CancellationTokenSource? lifeCts;
        Task? receiveTask;

        lock (_sync)
        {
            _disconnectRequested = true;
            _connected = false;
            connection = _connection;
            _connection = null;
            lifeCts = _lifeCts;
            _lifeCts = null;
            receiveTask = _receiveTask;
            _receiveTask = null;
        }

        if (connection is null && lifeCts is null)
        {
            return;
        }

        if (connection is not null)
        {
            // 尽力优雅退出；连接已断/实现不支持时忽略，关闭流程不能因 QUIT 失败而中断。
            try
            {
                await connection.SendAsync(QuitLine + LineEnding, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // QUIT 是 best-effort，失败不影响本地释放。
            }
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
            connection?.Abort();
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
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
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

    private async Task ReceiveLoopAsync(ITwitchIrcConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null)
                {
                    // 对端正常关闭（Close 帧）：Disconnected 参数为 null。
                    SignalDisconnected(null);
                    return;
                }

                foreach (var message in TwitchIrcParser.ParseFrame(frame))
                {
                    switch (message.Command)
                    {
                        case "PING":
                            await connection.SendAsync(PongLine + LineEnding, ct).ConfigureAwait(false);
                            break;

                        case "RECONNECT":
                            // 服务端要求重连：停止当前循环并通知上层（由 DanmakuManager 负责新建连接）。
                            SignalDisconnected(null);
                            return;

                        case "PRIVMSG":
                            var danmaku = TwitchIrcParser.ToDanmakuMessage(message, _clock);
                            if (danmaku is not null)
                            {
                                RaiseMessageReceived(danmaku);
                            }

                            break;

                        default:
                            // JOIN/001/366/USERNOTICE 等与弹幕无关的消息忽略。
                            break;
                    }
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

    /// <summary>连接异常断开：只投递一次 Disconnected，并中止连接、停掉接收循环。</summary>
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

        ITwitchIrcConnection? connection;
        CancellationTokenSource? lifeCts;
        lock (_sync)
        {
            _connected = false;
            connection = _connection;
            lifeCts = _lifeCts;
        }

        try
        {
            connection?.Abort();
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
            // 订阅方异常不能拖垮后台收包循环。
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
            throw new ObjectDisposedException(nameof(TwitchDanmakuClient));
        }
    }
}
