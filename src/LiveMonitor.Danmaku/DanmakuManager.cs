using System.Threading.Channels;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku.Analyzers;
using LiveMonitor.Danmaku.Filters;

namespace LiveMonitor.Danmaku;

/// <summary>(平台, 归一化房间号) 的会话键；房间号去首尾空白并统一大写，实现 OrdinalIgnoreCase 语义。</summary>
internal readonly record struct RoomKey(PlatformType Platform, string RoomId)
{
    public static RoomKey Create(PlatformType platform, string roomId)
        => new(platform, roomId.Trim().ToUpperInvariant());
}

/// <summary>
/// 弹幕会话管理器：按 (平台, 房间号) 引用计数共享一条直播间连接，统一驱动
/// KeywordFilter → BilibiliUnicycleFilter → SlidingWindowDetector → 批量 UI 事件 管线，
/// 并负责断线退避重连、设置热更新与高能事件异步落盘。
/// <para>
/// 使用顺序：先 <see cref="InitializeAsync"/> 读取屏蔽词/独轮车开关/高能规则，
/// 再 <see cref="Acquire"/> 获取会话句柄；句柄 Dispose 到引用计数归零时后台断连。
/// 所有消息与高能事件都可能来自后台线程，订阅方自行切到 UI 线程。
/// </para>
/// </summary>
public sealed class DanmakuManager : IAsyncDisposable
{
    /// <summary>断线重连退避序列：3s → 10s → 30s → 60s，之后固定 60s。</summary>
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    /// <summary>一次连接持续超过该时长后，断线时把退避重置回 3 秒。</summary>
    private static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(30);

    private readonly IDanmakuClientFactory _clientFactory;
    private readonly ICredentialStore _credentials;
    private readonly IBlocklistRepository _blocklistRepository;
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly IHighlightEventRepository _highlightEventRepository;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _batchInterval;
    private readonly Action<string>? _log;

    private readonly object _gate = new();
    private readonly Dictionary<RoomKey, RoomSession> _sessions = new();

    private readonly Channel<HighlightEvent> _highlightChannel = Channel.CreateUnbounded<HighlightEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private readonly Task _highlightPersistenceTask;

    private IReadOnlyList<DanmakuBlockKeyword> _keywords = Array.Empty<DanmakuBlockKeyword>();
    private HighlightRuleOptions _highlightRules = HighlightRuleOptions.Default;
    private bool _unicycleEnabled;
    private bool _initialized;
    private int _disposed;
    private readonly object _unicycleUsersGate = new();
    private readonly HashSet<(PlatformType Platform, string SenderUid)> _unicycleBlockedUsers = new();

    /// <summary>
    /// 创建弹幕会话管理器。构造后仍需 <see cref="InitializeAsync"/> 才能 <see cref="Acquire"/>。
    /// </summary>
    /// <param name="clientFactory">按平台创建弹幕客户端；不支持平台返回 null。</param>
    /// <param name="credentials">Cookie 存取，用于每次连接前读取登录态。</param>
    /// <param name="blocklist">弹幕屏蔽词仓储，Initialize/Reload 时整表读取。</param>
    /// <param name="settings">AppSettings 仓储，读取独轮车开关与高能规则。</param>
    /// <param name="highlightEvents">高能事件仓储，用于异步落盘与历史查询。</param>
    /// <param name="delay">可注入的等待函数（默认 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>），测试用。</param>
    /// <param name="clock">可注入的时钟（默认系统 UTC 时间），用于稳定连接判定与检测器。</param>
    /// <param name="batchInterval">弹幕批量事件的合帧周期，默认 50ms。</param>
    /// <param name="log">可选的诊断日志回调。</param>
    public DanmakuManager(
        IDanmakuClientFactory clientFactory,
        ICredentialStore credentials,
        IBlocklistRepository blocklist,
        IAppSettingsRepository settings,
        IHighlightEventRepository highlightEvents,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? batchInterval = null,
        Action<string>? log = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _blocklistRepository = blocklist ?? throw new ArgumentNullException(nameof(blocklist));
        _settingsRepository = settings ?? throw new ArgumentNullException(nameof(settings));
        _highlightEventRepository = highlightEvents ?? throw new ArgumentNullException(nameof(highlightEvents));
        _delay = delay ?? ((interval, cancellationToken) => Task.Delay(interval, cancellationToken));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _batchInterval = batchInterval ?? TimeSpan.FromMilliseconds(50);
        if (_batchInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchInterval), "批处理间隔必须大于零。");
        }

        _log = log;
        _highlightPersistenceTask = Task.Run(HighlightPersistenceLoopAsync);
    }

    /// <summary>每次检测到高能时触发（同步于消息管线线程）；随后由管理器异步落盘。</summary>
    public event Action<HighlightEvent>? HighlightTriggered;

    /// <summary>全局累计被拦截的独轮车用户数变化时触发，参数为变化后的累计值（非增量）。</summary>
    public event Action<int>? UnicycleBlockedUserCountChanged;

    /// <summary>全局累计被独轮车过滤器拦截的不同用户数（跨所有会话、进程生命周期内累加）。</summary>
    public int UnicycleBlockedUserCount
    {
        get
        {
            lock (_unicycleUsersGate)
            {
                return _unicycleBlockedUsers.Count;
            }
        }
    }

    /// <summary>
    /// 读取屏蔽词、独轮车开关与高能规则。可重复调用（幂等），会热更新已存在的会话；
    /// 调用前 <see cref="Acquire"/> 会抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var loaded = await LoadSettingsAsync(ct).ConfigureAwait(false);
        RoomSession[] sessions;
        lock (_gate)
        {
            ThrowIfDisposed();
            _keywords = loaded.Keywords;
            _unicycleEnabled = loaded.UnicycleEnabled;
            _highlightRules = loaded.Rules;
            _initialized = true;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            ApplySettings(session, loaded.Keywords, loaded.UnicycleEnabled, loaded.Rules);
        }
    }

    /// <summary>
    /// 获取指定房间的会话句柄（引用计数 +1）。首次获取会在后台启动连接，不阻塞调用线程；
    /// 同 key 重复获取只增加引用计数，不新建连接。每次 Acquire 必须对应一次 <see cref="DanmakuSessionHandle.Dispose"/>。
    /// </summary>
    /// <exception cref="InvalidOperationException">尚未调用 <see cref="InitializeAsync"/>。</exception>
    public DanmakuSessionHandle Acquire(PlatformType platform, string roomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        var trimmedRoomId = roomId.Trim();
        var key = RoomKey.Create(platform, trimmedRoomId);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                throw new InvalidOperationException("请先 InitializeAsync");
            }

            RoomSession session;
            var created = false;
            if (_sessions.TryGetValue(key, out var existing))
            {
                session = existing;
            }
            else
            {
                session = CreateSessionLocked(platform, trimmedRoomId, key);
                _sessions.Add(key, session);
                created = true;
            }

            var handle = new DanmakuSessionHandle(this, key, platform, trimmedRoomId);
            lock (session.Gate)
            {
                session.Handles.Add(handle);
            }

            if (created)
            {
                StartSessionLocked(session);
            }

            return handle;
        }
    }

    /// <summary>
    /// 重新读取屏蔽词、独轮车开关与高能规则，热更新所有已存在会话（不重建连接）。
    /// 尚未 Initialize 时只保存设置，不会让 Acquire 变为可用。
    /// </summary>
    public async Task ReloadSettingsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var loaded = await LoadSettingsAsync(ct).ConfigureAwait(false);
        RoomSession[] sessions;
        lock (_gate)
        {
            ThrowIfDisposed();
            _keywords = loaded.Keywords;
            _unicycleEnabled = loaded.UnicycleEnabled;
            _highlightRules = loaded.Rules;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            ApplySettings(session, loaded.Keywords, loaded.UnicycleEnabled, loaded.Rules);
        }
    }

    /// <summary>
    /// 持久化高能检测参数并热更新所有会话的检测器（保留已有窗口数据）。
    /// </summary>
    public async Task UpdateHighlightRulesAsync(HighlightRuleOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        var normalized = options.Normalize();
        await HighlightSettingsStore.SaveAsync(_settingsRepository, normalized, ct).ConfigureAwait(false);

        RoomSession[] sessions;
        lock (_gate)
        {
            ThrowIfDisposed();
            _highlightRules = normalized;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            session.Detector.UpdateRules(normalized);
        }
    }

    /// <summary>
    /// 持久化独轮车过滤开关并同步到所有会话的过滤器。
    /// </summary>
    /// <returns>写入成功固定返回 true。</returns>
    public async Task<bool> SetUnicycleEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _settingsRepository.SetAsync(
            AppSettingKeys.BlockUnicycleEnabled,
            enabled ? "true" : "false",
            ct).ConfigureAwait(false);

        RoomSession[] sessions;
        lock (_gate)
        {
            ThrowIfDisposed();
            _unicycleEnabled = enabled;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            session.UnicycleFilter.IsEnabled = enabled;
        }

        return true;
    }

    /// <summary>查询最近的高能事件（转调 <see cref="IHighlightEventRepository.GetRecentAsync"/>）。</summary>
    public Task<IReadOnlyList<HighlightEvent>> GetRecentHighlightEventsAsync(
        int limit = 200,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _highlightEventRepository.GetRecentAsync(limit, ct);
    }

    /// <summary>
    /// 停止全部连接、flush 待落盘高能事件并释放资源。可重复调用（幂等）。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RoomSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                session.Cts.Cancel();
            }
            catch (Exception ex)
            {
                Log($"[{session.Platform}:{session.RoomId}] 停止会话时取消失败：{ex.Message}");
            }
        }

        foreach (var session in sessions)
        {
            await AwaitTaskQuietlyAsync(session.ConnectionTask).ConfigureAwait(false);
            await AwaitTaskQuietlyAsync(session.BatchTask).ConfigureAwait(false);
            try
            {
                session.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // 关闭落盘通道并等待队列中剩余的高能事件写入仓储。
        _highlightChannel.Writer.TryComplete();
        await AwaitTaskQuietlyAsync(_highlightPersistenceTask).ConfigureAwait(false);
    }

    /// <summary>由会话句柄 Dispose 调用：引用计数 -1，归零时后台停止连接与批处理。</summary>
    internal void ReleaseHandle(DanmakuSessionHandle handle)
    {
        RoomSession? sessionToStop = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!_sessions.TryGetValue(handle.Key, out var session))
            {
                return;
            }

            var lastReference = false;
            lock (session.Gate)
            {
                if (session.Handles.Remove(handle))
                {
                    lastReference = session.Handles.Count == 0;
                }
            }

            if (lastReference)
            {
                _sessions.Remove(handle.Key);
                sessionToStop = session;
            }
        }

        if (sessionToStop is not null)
        {
            _ = Task.Run(() => StopSessionAsync(sessionToStop));
        }
    }

    /// <summary>供句柄记录订阅方异常，避免一个坏订阅者打断整个弹幕管线。</summary>
    internal void ReportHandlerError(string message) => Log(message);

    private async Task<(IReadOnlyList<DanmakuBlockKeyword> Keywords, bool UnicycleEnabled, HighlightRuleOptions Rules)>
        LoadSettingsAsync(CancellationToken ct)
    {
        var keywords = await _blocklistRepository.GetAllAsync(ct).ConfigureAwait(false);
        var unicycleEnabled = await ReadUnicycleEnabledAsync(ct).ConfigureAwait(false);
        var rules = await HighlightSettingsStore.LoadAsync(_settingsRepository, ct).ConfigureAwait(false);
        return (keywords.ToArray(), unicycleEnabled, rules);
    }

    private async Task<bool> ReadUnicycleEnabledAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settingsRepository.GetAsync(AppSettingKeys.BlockUnicycleEnabled, ct).ConfigureAwait(false);
            return ParseBoolean(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"读取独轮车开关失败，按关闭处理：{ex.Message}");
            return false;
        }
    }

    private static bool ParseBoolean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (bool.TryParse(trimmed, out var value))
        {
            return value;
        }

        return trimmed == "1";
    }

    /// <summary>必须在持有 <see cref="_gate"/> 时调用。</summary>
    private RoomSession CreateSessionLocked(PlatformType platform, string roomId, RoomKey key)
    {
        var session = new RoomSession(platform, roomId, key)
        {
            KeywordFilter = new KeywordFilter(_keywords, _log),
            UnicycleFilter = new BilibiliUnicycleFilter(_clock) { IsEnabled = _unicycleEnabled },
            Detector = new SlidingWindowDetector(_highlightRules, _clock),
        };

        session.Detector.HighlightTriggered += highlightEvent => OnHighlightDetected(session, highlightEvent);
        return session;
    }

    /// <summary>必须在持有 <see cref="_gate"/> 时调用；仅后台启动，不阻塞调用线程。</summary>
    private void StartSessionLocked(RoomSession session)
    {
        session.ConnectionTask = Task.Run(() => ConnectionLoopAsync(session));
        session.BatchTask = Task.Run(() => BatchLoopAsync(session));
    }

    private void ApplySettings(
        RoomSession session,
        IReadOnlyList<DanmakuBlockKeyword> keywords,
        bool unicycleEnabled,
        HighlightRuleOptions rules)
    {
        session.KeywordFilter.Update(keywords);
        session.UnicycleFilter.IsEnabled = unicycleEnabled;
        session.Detector.UpdateRules(rules);
    }

    private async Task ConnectionLoopAsync(RoomSession session)
    {
        var ct = session.Cts.Token;
        var backoffIndex = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                IDanmakuClient? client = null;
                var connected = false;
                var connectedAt = default(DateTimeOffset);
                TaskCompletionSource<Exception?>? disconnected = null;
                Action<DanmakuMessage>? onMessage = null;
                Action<Exception?>? onDisconnected = null;

                try
                {
                    var cookie = await GetCookieAsync(session.Platform, ct).ConfigureAwait(false);
                    client = _clientFactory.Create(session.Platform, session.RoomId, cookie);
                    if (client is null)
                    {
                        Log($"[{session.Platform}:{session.RoomId}] 该平台暂不支持弹幕，停止连接尝试。");
                        return;
                    }

                    disconnected = new TaskCompletionSource<Exception?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    onMessage = message => HandleMessage(session, message);
                    var completion = disconnected;
                    onDisconnected = error => completion.TrySetResult(error);

                    client.MessageReceived += onMessage;
                    client.Disconnected += onDisconnected;

                    await client.ConnectAsync(ct).ConfigureAwait(false);
                    connected = true;
                    connectedAt = _clock();
                    Log($"[{session.Platform}:{session.RoomId}] 弹幕连接成功。");

                    var disconnectError = await disconnected.Task.WaitAsync(ct).ConfigureAwait(false);
                    Log(disconnectError is null
                        ? $"[{session.Platform}:{session.RoomId}] 弹幕连接被服务器关闭，准备重连。"
                        : $"[{session.Platform}:{session.RoomId}] 弹幕连接异常断开（{disconnectError.Message}），准备重连。");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"[{session.Platform}:{session.RoomId}] 弹幕连接失败：{ex.Message}");
                }
                finally
                {
                    if (client is not null)
                    {
                        if (onMessage is not null)
                        {
                            client.MessageReceived -= onMessage;
                        }

                        if (onDisconnected is not null)
                        {
                            client.Disconnected -= onDisconnected;
                        }

                        try
                        {
                            await client.DisconnectAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"[{session.Platform}:{session.RoomId}] 断开弹幕连接失败：{ex.Message}");
                        }

                        try
                        {
                            await client.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"[{session.Platform}:{session.RoomId}] 释放弹幕客户端失败：{ex.Message}");
                        }
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 持续稳定超过阈值后，断线重连从头开始退避，避免长期连接偶发断开直接退到 60 秒。
                if (connected && _clock() - connectedAt >= StableConnectionThreshold)
                {
                    backoffIndex = 0;
                }

                var wait = BackoffSchedule[Math.Min(backoffIndex, BackoffSchedule.Length - 1)];
                backoffIndex = Math.Min(backoffIndex + 1, BackoffSchedule.Length - 1);
                Log($"[{session.Platform}:{session.RoomId}] {wait.TotalSeconds:0} 秒后重连。");

                try
                {
                    await _delay(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            Log($"[{session.Platform}:{session.RoomId}] 弹幕连接循环已退出。");
        }
    }

    private async Task<string?> GetCookieAsync(PlatformType platform, CancellationToken ct)
    {
        try
        {
            var cookie = await _credentials.GetCookieAsync(platform, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(cookie) ? null : cookie;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[{platform}] 读取 Cookie 失败，按未登录处理：{ex.Message}");
            return null;
        }
    }

    private void HandleMessage(RoomSession session, DanmakuMessage message)
    {
        if (message is null)
        {
            return;
        }

        bool allowed;
        try
        {
            allowed = session.KeywordFilter.ShouldAllow(message);
        }
        catch (Exception ex)
        {
            Log($"[{session.Platform}:{session.RoomId}] 关键词过滤异常，丢弃该条弹幕：{ex.Message}");
            return;
        }

        if (!allowed)
        {
            return; // 关键词屏蔽：静默丢弃，不计独轮车、不进入统计与渲染。
        }

        try
        {
            allowed = session.UnicycleFilter.ShouldAllow(message);
        }
        catch (Exception ex)
        {
            Log($"[{session.Platform}:{session.RoomId}] 独轮车过滤异常，放行该条弹幕：{ex.Message}");
            allowed = true;
        }

        if (!allowed)
        {
            OnUnicycleBlocked(session, message);
            return; // 独轮车屏蔽：不进入统计与渲染。
        }

        try
        {
            session.Detector.Ingest(message);
        }
        catch (Exception ex)
        {
            Log($"[{session.Platform}:{session.RoomId}] 高能检测异常：{ex.Message}");
        }

        lock (session.Gate)
        {
            session.BatchQueue.Enqueue(message);
        }
    }

    private void OnUnicycleBlocked(RoomSession session, DanmakuMessage message)
    {
        // 独轮车计数按「被拦截的不同用户数」统计：同一用户被拦多条只计 1 个用户。
        var senderUid = string.IsNullOrWhiteSpace(message.SenderUid) ? "0" : message.SenderUid;

        DanmakuSessionHandle[] handles;
        lock (session.Gate)
        {
            handles = session.Handles.ToArray();
        }

        foreach (var handle in handles)
        {
            if (!handle.IsDisposed)
            {
                handle.RecordBlockedUser(senderUid);
            }
        }

        int total;
        lock (_unicycleUsersGate)
        {
            if (!_unicycleBlockedUsers.Add((session.Platform, senderUid)))
            {
                return; // 该用户此前已被计数，不重复触发变更事件。
            }

            total = _unicycleBlockedUsers.Count;
        }

        var handler = UnicycleBlockedUserCountChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(total);
        }
        catch (Exception ex)
        {
            Log($"独轮车计数订阅者异常：{ex.Message}");
        }
    }

    private void OnHighlightDetected(RoomSession session, HighlightEvent highlightEvent)
    {
        var handler = HighlightTriggered;
        if (handler is not null)
        {
            try
            {
                handler(highlightEvent);
            }
            catch (Exception ex)
            {
                Log($"[{session.Platform}:{session.RoomId}] 高能事件订阅者异常：{ex.Message}");
            }
        }

        if (!_highlightChannel.Writer.TryWrite(highlightEvent))
        {
            Log($"[{session.Platform}:{session.RoomId}] 高能事件未能写入落盘队列（管理器已释放）。");
        }
    }

    private async Task HighlightPersistenceLoopAsync()
    {
        try
        {
            await foreach (var highlightEvent in _highlightChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _highlightEventRepository
                        .AddAsync(highlightEvent, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 落盘失败不能反向影响消息管线。
                    Log($"高能事件落盘失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"高能事件落盘循环异常退出：{ex.Message}");
        }
    }

    private async Task BatchLoopAsync(RoomSession session)
    {
        var ct = session.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(_batchInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            DeliverBatch(session);
        }
    }

    private void DeliverBatch(RoomSession session)
    {
        List<DanmakuMessage>? pending = null;
        DanmakuSessionHandle[] handles;
        lock (session.Gate)
        {
            if (session.BatchQueue.Count == 0)
            {
                return;
            }

            pending = new List<DanmakuMessage>(session.BatchQueue);
            session.BatchQueue.Clear();
            handles = session.Handles.ToArray();
        }

        IReadOnlyList<DanmakuMessage> batch = pending;
        foreach (var handle in handles)
        {
            if (handle.IsDisposed)
            {
                continue;
            }

            handle.IncrementMessageCount(batch.Count);
            handle.RaiseMessages(batch);
        }
    }

    private async Task StopSessionAsync(RoomSession session)
    {
        try
        {
            session.Cts.Cancel();
        }
        catch (Exception ex)
        {
            Log($"[{session.Platform}:{session.RoomId}] 停止会话时取消失败：{ex.Message}");
        }

        await AwaitTaskQuietlyAsync(session.ConnectionTask).ConfigureAwait(false);
        await AwaitTaskQuietlyAsync(session.BatchTask).ConfigureAwait(false);

        try
        {
            session.Cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AwaitTaskQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"后台任务异常退出：{ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DanmakuManager));
        }
    }

    private void Log(string message)
    {
        var log = _log;
        if (log is null)
        {
            return;
        }

        try
        {
            log(message);
        }
        catch
        {
            // 日志回调本身不能影响弹幕管线。
        }
    }

    /// <summary>单个 (平台, 房间号) 会话的运行时状态。</summary>
    private sealed class RoomSession
    {
        public RoomSession(PlatformType platform, string roomId, RoomKey key)
        {
            Platform = platform;
            RoomId = roomId;
            Key = key;
        }

        public PlatformType Platform { get; }

        public string RoomId { get; }

        public RoomKey Key { get; }

        /// <summary>保护句柄集合与批处理队列。</summary>
        public object Gate { get; } = new();

        public List<DanmakuSessionHandle> Handles { get; } = new();

        public Queue<DanmakuMessage> BatchQueue { get; } = new();

        public CancellationTokenSource Cts { get; } = new();

        public KeywordFilter KeywordFilter { get; set; } = null!;

        public BilibiliUnicycleFilter UnicycleFilter { get; set; } = null!;

        public SlidingWindowDetector Detector { get; set; } = null!;

        public Task? ConnectionTask { get; set; }

        public Task? BatchTask { get; set; }
    }
}
