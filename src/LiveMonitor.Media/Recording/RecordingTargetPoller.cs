using System.Globalization;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 开播轮询器与录制中心之间的窄接口：轮询器只通过它做「是否在录/是否处于抑制期」判定、
/// 清理抑制状态、把开播交给录制中心起录，并复用录制中心的日志与状态提示出口。
/// <para>
/// 这样 <see cref="RecordingTargetPoller"/> 不需要反向依赖 <see cref="RecordingManager"/> 实例，
/// 避免轮询循环与会话状态机之间形成循环依赖。
/// </para>
/// </summary>
internal interface IRecordingTargetPollerHost
{
    /// <summary>直播间当前是否正在录制（用于跳过已起录的房间）。</summary>
    bool IsRecording(Guid liveRoomId);

    /// <summary>直播间是否处于自动重开抑制期（手动停止后等待下一次下播）。</summary>
    bool IsAutoRestartSuppressed(Guid liveRoomId);

    /// <summary>探测到未开播：清除抑制与断流续录状态，允许下次开播重新自动起录。</summary>
    void ResetRoomPollingState(Guid liveRoomId);

    /// <summary>探测到开播且未被抑制：交给录制中心起录。</summary>
    Task StartAutoRecordingAsync(
        LiveRoom room,
        IPlatformProvider provider,
        string? cookie,
        RoomStatus status,
        CancellationToken ct);

    /// <summary>输出轮询日志（与录制中心共用同一日志出口）。</summary>
    void Log(string message);

    /// <summary>输出运行期状态提示（自动录制失败等），供界面状态栏展示。</summary>
    void NotifyStatus(string message);

    /// <summary>格式化直播间展示名（#序号 主播名）。</summary>
    string FormatRoomDisplayName(LiveRoom room);
}

/// <summary>
/// 开播轮询器：读取直播间与录制偏好，按固定节拍并发探测开播状态，发现开播后通过
/// <see cref="IRecordingTargetPollerHost"/> 交回录制中心起录。
/// <para>
/// 职责边界：轮询循环、初始延迟与轮询间隔节流、开播状态探测的并发上限（同一时刻最多
/// <see cref="MaxConcurrentPolls"/> 个探测）、探测失败/取消的吞噬与日志、抑制状态判定时机。
/// 录制会话状态、断流续录、收尾恢复仍由 <see cref="RecordingManager"/> 持有。
/// </para>
/// </summary>
internal sealed class RecordingTargetPoller : IAsyncDisposable
{
    /// <summary>缺省开播轮询间隔（秒）。</summary>
    internal const int DefaultPollIntervalSeconds = 60;

    internal const int MinPollIntervalSeconds = 10;

    internal const int MaxPollIntervalSeconds = 600;

    /// <summary>同一时刻允许并发执行的开播状态探测数（原 RecordingManager._pollGate 并发度）。</summary>
    internal const int MaxConcurrentPolls = 4;

    /// <summary>启动后首次轮询前的等待时间，避免与应用启动争抢 IO。</summary>
    internal static readonly TimeSpan InitialPollDelay = TimeSpan.FromSeconds(3);

    private readonly IRoomRepository _rooms;
    private readonly IRecordingTargetRepository _targets;
    private readonly IAppSettingsRepository _settings;
    private readonly Func<PlatformType, IPlatformProvider?> _providerResolver;
    private readonly Func<PlatformType, CancellationToken, Task<string?>> _readCookie;
    private readonly IRecordingTargetPollerHost _host;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan? _pollIntervalOverride;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _pollGate = new(MaxConcurrentPolls, MaxConcurrentPolls);

    private TimeSpan _pollInterval = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
    private Task? _loopTask;
    private int _disposed;

    public RecordingTargetPoller(
        IRoomRepository rooms,
        IRecordingTargetRepository targets,
        IAppSettingsRepository settings,
        Func<PlatformType, IPlatformProvider?> providerResolver,
        Func<PlatformType, CancellationToken, Task<string?>> readCookie,
        IRecordingTargetPollerHost host,
        TimeSpan? pollIntervalOverride = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _readCookie = readCookie ?? throw new ArgumentNullException(nameof(readCookie));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _delay = delay ?? ((timeSpan, token) => Task.Delay(timeSpan, token));
        _pollIntervalOverride = pollIntervalOverride;
        if (_pollIntervalOverride is { } pollInterval)
        {
            _pollInterval = ClampPollInterval(pollInterval);
        }
    }

    /// <summary>当前开播轮询间隔。</summary>
    internal TimeSpan PollInterval
    {
        get
        {
            lock (_gate)
            {
                return _pollInterval;
            }
        }
    }

    /// <summary>
    /// 读取并应用轮询间隔配置；构造时已显式指定间隔（测试/宿主覆盖）时是空操作。
    /// 在 <see cref="Start"/> 之前调用。
    /// </summary>
    internal async Task ConfigureAsync(CancellationToken ct = default)
    {
        if (_pollIntervalOverride is not null)
        {
            return;
        }

        var interval = await ReadPollIntervalAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _pollInterval = interval;
        }
    }

    /// <summary>启动轮询循环；<paramref name="token"/> 取消后循环退出。</summary>
    internal void Start(CancellationToken token)
    {
        lock (_gate)
        {
            if (_loopTask is not null)
            {
                throw new InvalidOperationException("开播轮询循环已启动。");
            }

            _loopTask = Task.Run(() => PollLoopAsync(token), token);
        }
    }

    /// <summary>等待轮询循环退出（调用前应先取消轮询 token）；循环内部异常已记录，这里不抛出。</summary>
    internal async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loopTask;
        }

        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 轮询循环已自行记录异常，这里只等待退出。
        }
    }

    /// <summary>释放并发闸门；应在 <see cref="StopAsync"/> 之后调用。</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pollGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 执行一轮开播检查：读取主库与录制偏好，对所有开启自动录制且不在录的直播间查询开播状态并起录。
    /// 自动轮询循环也调用本方法。
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LiveRoom> rooms;
        IReadOnlyList<RecordingTarget> targets;
        try
        {
            rooms = await _rooms.GetAllAsync(ct).ConfigureAwait(false);
            targets = await _targets.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host.Log($"读取直播间/录制偏好失败，本轮开播检查跳过：{ex.Message}");
            return;
        }

        var roomMap = new Dictionary<Guid, LiveRoom>();
        foreach (var room in rooms)
        {
            roomMap[room.Id] = room;
        }

        var tasks = new List<Task>();
        foreach (var target in targets)
        {
            if (!target.AutoRecordEnabled || !roomMap.TryGetValue(target.LiveRoomId, out var room))
            {
                continue;
            }

            if (_host.IsRecording(room.Id))
            {
                continue;
            }

            tasks.Add(PollRoomAsync(room, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            await _delay(InitialPollDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _host.Log($"开播轮询失败：{ex.Message}");
                }

                await _delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollRoomAsync(LiveRoom room, CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host.IsRecording(room.Id))
            {
                return;
            }

            var provider = _providerResolver(room.Platform);
            if (provider is null)
            {
                _host.Log($"未注册平台 {room.Platform} 的 Provider，跳过自动录制检查。");
                return;
            }

            // 自定义清流没有开播查询接口（GetRoomStatusAsync 恒返回 live），自动录制会导致
            // 程序启动即开录、失败后无限重试；清流只支持手动开录。
            if (room.Platform == PlatformType.CustomStream)
            {
                return;
            }

            var cookie = await _readCookie(room.Platform, ct).ConfigureAwait(false);
            var status = await provider.GetRoomStatusAsync(room.RoomId, cookie, ct).ConfigureAwait(false);
            if (!status.IsLive)
            {
                _host.ResetRoomPollingState(room.Id);
                return;
            }

            if (_host.IsAutoRestartSuppressed(room.Id))
            {
                return;
            }

            await _host.StartAutoRecordingAsync(room, provider, cookie, status, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _host.Log($"自动录制检查失败（{_host.FormatRoomDisplayName(room)}）：{ex.Message}");
            _host.NotifyStatus($"自动录制失败（{_host.FormatRoomDisplayName(room)}）：{ex.Message}");
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task<TimeSpan> ReadPollIntervalAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settings.GetAsync(AppSettingKeys.RecordingPollIntervalSeconds, ct).ConfigureAwait(false);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return ClampPollInterval(TimeSpan.FromSeconds(seconds));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host.Log($"读取录制轮询间隔失败，使用默认值：{ex.Message}");
        }

        return TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
    }

    private static TimeSpan ClampPollInterval(TimeSpan value)
    {
        var seconds = (int)Math.Round(value.TotalSeconds);
        seconds = Math.Clamp(seconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
