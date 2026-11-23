using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Tests;

/// <summary>
/// 测试用弹幕客户端：可手动抛出 <see cref="MessageReceived"/>/<see cref="Disconnected"/>，
/// 记录 Connect/Disconnect/Dispose 次数，用于验证 DanmakuManager 的连接生命周期与重连。
/// </summary>
public sealed class FakeDanmakuClient : IDanmakuClient
{
    private readonly TaskCompletionSource<bool> _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _connectCount;
    private int _disconnectCount;
    private int _disposeCount;

    public FakeDanmakuClient(PlatformType platform, string roomId)
    {
        Platform = platform;
        RoomId = roomId;
    }

    public PlatformType Platform { get; }

    public string RoomId { get; }

    public event Action<DanmakuMessage>? MessageReceived;

    public event Action<Exception?>? Disconnected;

    /// <summary>非 null 时 <see cref="ConnectAsync"/> 抛出该异常。</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>可选的自定义连接行为（优先于 <see cref="ConnectException"/>）。</summary>
    public Func<CancellationToken, Task>? ConnectHandler { get; set; }

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public int DisconnectCount => Volatile.Read(ref _disconnectCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public bool IsConnected { get; private set; }

    /// <summary>成功连接后完成，测试可等待它确认连接已建立。</summary>
    public Task ConnectedTask => _connected.Task;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _connectCount);
        ct.ThrowIfCancellationRequested();

        if (ConnectHandler is not null)
        {
            return ConnectHandler(ct);
        }

        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        IsConnected = true;
        _connected.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Interlocked.Increment(ref _disconnectCount);
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>模拟收到一条弹幕。</summary>
    public void RaiseMessage(DanmakuMessage message) => MessageReceived?.Invoke(message);

    /// <summary>模拟连接断开；null 表示对端正常关闭。</summary>
    public void RaiseDisconnected(Exception? error = null) => Disconnected?.Invoke(error);
}

/// <summary>测试用客户端工厂：记录 Create 调用、Cookie 与创建的客户端，可模拟失败或返回 null。</summary>
public sealed class FakeDanmakuClientFactory : IDanmakuClientFactory
{
    private readonly object _gate = new();
    private readonly List<FakeDanmakuClient> _clients = new();
    private int _createCount;
    private PlatformType? _lastPlatform;
    private string? _lastRoomId;
    private string? _lastCookie;

    /// <summary>true 时 Create 固定返回 null（模拟平台暂不支持弹幕）。</summary>
    public bool ReturnNull { get; set; }

    /// <summary>按创建序号（0 起）返回要让该客户端连接失败的异常；返回 null 表示连接成功。</summary>
    public Func<int, Exception?>? ConnectFailureOnAttempt { get; set; }

    public int CreateCount => Volatile.Read(ref _createCount);

    public PlatformType? LastPlatform
    {
        get
        {
            lock (_gate)
            {
                return _lastPlatform;
            }
        }
    }

    public string? LastRoomId
    {
        get
        {
            lock (_gate)
            {
                return _lastRoomId;
            }
        }
    }

    public string? LastCookie
    {
        get
        {
            lock (_gate)
            {
                return _lastCookie;
            }
        }
    }

    public IReadOnlyList<FakeDanmakuClient> Clients
    {
        get
        {
            lock (_gate)
            {
                return _clients.ToArray();
            }
        }
    }

    public FakeDanmakuClient? TryGetClient(int index)
    {
        lock (_gate)
        {
            return index >= 0 && index < _clients.Count ? _clients[index] : null;
        }
    }

    public IDanmakuClient? Create(PlatformType platform, string roomId, string? cookie)
    {
        var index = Interlocked.Increment(ref _createCount) - 1;
        lock (_gate)
        {
            _lastPlatform = platform;
            _lastRoomId = roomId;
            _lastCookie = cookie;
        }

        if (ReturnNull)
        {
            return null;
        }

        var client = new FakeDanmakuClient(platform, roomId)
        {
            ConnectException = ConnectFailureOnAttempt?.Invoke(index),
        };

        lock (_gate)
        {
            _clients.Add(client);
        }

        return client;
    }
}

/// <summary>测试用凭据仓储：可预置 Cookie，也可模拟读取异常。</summary>
public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<PlatformType, string> _cookies = new();

    public bool ThrowOnGetCookie { get; set; }

    public int GetCookieCalls { get; private set; }

    public void SetCookie(PlatformType platform, string? cookie)
    {
        lock (_gate)
        {
            if (cookie is null)
            {
                _cookies.Remove(platform);
            }
            else
            {
                _cookies[platform] = cookie;
            }
        }
    }

    public Task<string?> GetCookieAsync(PlatformType platform, CancellationToken ct = default)
    {
        lock (_gate)
        {
            GetCookieCalls++;
            if (ThrowOnGetCookie)
            {
                throw new InvalidOperationException("凭据仓储不可用（测试注入）");
            }

            return Task.FromResult(_cookies.TryGetValue(platform, out var cookie) ? cookie : null);
        }
    }

    public Task SaveAsync(
        PlatformType platform,
        string? accountName,
        string cookie,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveAsync(PlatformType platform, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cookies.Remove(platform);
        }

        return Task.CompletedTask;
    }

    public Task MarkExpiredAsync(PlatformType platform, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> IsExpiredAsync(PlatformType platform, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>测试用高能事件仓储：内存保存、可断言落盘数量，也可模拟写入失败。</summary>
public sealed class InMemoryHighlightEventRepository : IHighlightEventRepository
{
    private readonly object _gate = new();
    private readonly List<HighlightEvent> _events = new();

    public bool ThrowOnAdd { get; set; }

    public IReadOnlyList<HighlightEvent> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public Task AddAsync(HighlightEvent highlightEvent, CancellationToken ct = default)
    {
        if (ThrowOnAdd)
        {
            throw new IOException("高能事件落盘失败（测试注入）");
        }

        lock (_gate)
        {
            _events.Add(highlightEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HighlightEvent>> GetRecentAsync(int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<HighlightEvent> result = _events
                .OrderByDescending(highlightEvent => highlightEvent.TriggerTime)
                .Take(Math.Max(limit, 0))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<HighlightEvent>> GetByRecordingSessionAsync(
        Guid sessionId,
        int limit = 500,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<HighlightEvent> result = _events
                .Where(highlightEvent => highlightEvent.RecordingSessionId == sessionId)
                .OrderBy(highlightEvent => highlightEvent.RecordingOffsetMs ?? 0)
                .Take(Math.Max(limit, 0))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Clear();
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 可注入的等待函数记录器：记录每次 delay 的时长；批处理等待只做 1ms 真实延迟，
/// 退避等待默认立即返回（可开启 <see cref="BlockBackoff"/> 阻塞到取消），让重连测试无需真实等待。
/// </summary>
public sealed class RecordingDelay
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _waits = new();

    public RecordingDelay(TimeSpan batchInterval)
    {
        BatchInterval = batchInterval;
    }

    public TimeSpan BatchInterval { get; }

    /// <summary>true 时非批处理的 delay 会一直阻塞到取消（用于验证退出逻辑）。</summary>
    public bool BlockBackoff { get; set; }

    public Func<TimeSpan, CancellationToken, Task> Handler => HandleAsync;

    /// <summary>全部 delay 时长（含批处理间隔），按发生顺序。</summary>
    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_gate)
            {
                return _waits.ToArray();
            }
        }
    }

    /// <summary>仅退避等待时长（已剔除批处理间隔），按发生顺序。</summary>
    public IReadOnlyList<TimeSpan> BackoffWaits
    {
        get
        {
            lock (_gate)
            {
                return _waits.Where(wait => wait != BatchInterval).ToArray();
            }
        }
    }

    private async Task HandleAsync(TimeSpan interval, CancellationToken ct)
    {
        lock (_gate)
        {
            _waits.Add(interval);
        }

        if (interval == BatchInterval)
        {
            // 批处理节拍：真实地让出 1ms，避免后台循环空转占满 CPU。
            await Task.Delay(1, ct).ConfigureAwait(false);
            return;
        }

        if (BlockBackoff)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>测试等待工具：轮询条件直到成立或超时，避免用真实长等待做断言。</summary>
public static class TestWait
{
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 5000,
        string? message = null)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"等待超时：{message ?? "条件未满足"}");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// DanmakuManager 测试脚手架：装配全部 Fake 依赖，并暴露等待客户端创建/连接完成的辅助方法。
/// </summary>
public sealed class DanmakuManagerHarness : IAsyncDisposable
{
    public static readonly TimeSpan DefaultBatchInterval = TimeSpan.FromMilliseconds(10);

    private readonly object _logGate = new();
    private readonly List<string> _logs = new();

    public DanmakuManagerHarness(TimeSpan? batchInterval = null)
    {
        BatchInterval = batchInterval ?? DefaultBatchInterval;
        Delay = new RecordingDelay(BatchInterval);
        Manager = new DanmakuManager(
            Factory,
            Credentials,
            Blocklist,
            Settings,
            Highlights,
            Delay.Handler,
            Clock.Reader,
            BatchInterval,
            message =>
            {
                lock (_logGate)
                {
                    _logs.Add(message);
                }
            });
    }

    public FakeClock Clock { get; } = new();

    public FakeDanmakuClientFactory Factory { get; } = new();

    public FakeCredentialStore Credentials { get; } = new();

    public InMemoryBlocklistRepository Blocklist { get; } = new();

    public InMemoryAppSettingsRepository Settings { get; } = new();

    public InMemoryHighlightEventRepository Highlights { get; } = new();

    public RecordingDelay Delay { get; }

    public DanmakuManager Manager { get; }

    public TimeSpan BatchInterval { get; }

    public IReadOnlyList<string> Logs
    {
        get
        {
            lock (_logGate)
            {
                return _logs.ToArray();
            }
        }
    }

    public Task InitializeAsync(CancellationToken ct = default) => Manager.InitializeAsync(ct);

    /// <summary>等待第 index 个客户端被创建并成功连接。</summary>
    public async Task<FakeDanmakuClient> WaitForClientAsync(int index = 0)
    {
        await TestWait.WaitUntilAsync(
            () => Factory.CreateCount > index,
            message: $"等待第 {index} 个弹幕客户端创建");

        var client = Factory.TryGetClient(index)
            ?? throw new InvalidOperationException($"第 {index} 个弹幕客户端不存在");
        await client.ConnectedTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return client;
    }

    /// <summary>等待至少 count 个客户端被创建（不要求连接成功）。</summary>
    public Task WaitForCreatedClientsAsync(int count)
        => TestWait.WaitUntilAsync(() => Factory.CreateCount >= count, message: $"等待 {count} 个弹幕客户端创建");

    public ValueTask DisposeAsync() => Manager.DisposeAsync();
}
