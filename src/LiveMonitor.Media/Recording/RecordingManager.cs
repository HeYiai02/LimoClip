using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 一路正在进行的录制的内存状态快照（供录制中心界面展示）。
/// </summary>
public sealed record ActiveRecordingSnapshot(LiveRoom Room, RecordingProgress Progress);

/// <summary>
/// 录制引擎：独立于监控工作区的录制中心状态机。
/// <para>
/// 职责：按手动/开播自动两种入口启动录制；用 <see cref="IFullStreamRecorder"/> 拉最高画质原画写
/// .mkv，并通过 <see cref="DanmakuJsonlRecorder"/> 写同名 .jsonl；把录制期间的高能事件通过
/// <see cref="RecordingHighlightTracker"/> 绑定到会话；监控进度心跳、首帧锚点、断流续录与崩溃恢复。
/// </para>
/// <para>
/// 单场录制的会话内状态机（分段录制器、分片清单、弹幕文件、进度心跳与收尾合并）由
/// <see cref="RecordingSessionWorker"/> 持有；本类只保留全局簿记：活动表、手动停止抑制、
/// 断流续录队列、启动闸门与对外 API/事件桥接。
/// </para>
/// <para>
/// 依赖 <see cref="DanmakuManager"/> 共享同房间的弹幕连接（监控与录制同房间只有一条 WebSocket）；
/// DanmakuManager 可以为 null（例如无弹幕场景），此时不写弹幕伴随文件。
/// </para>
/// </summary>
public sealed class RecordingManager
    : IAsyncDisposable, ILiveRecordingMonitor, IRecordingTargetPollerHost, IRecordingSessionWorkerHost
{
    /// <summary>缺省保存目录的末尾相对路径，拼接在用户目录下。</summary>
    public const string DefaultRelativeDirectory = @"Videos\LimoClip";

    /// <summary>缺省开播轮询间隔（秒）。</summary>
    public const int DefaultPollIntervalSeconds = RecordingTargetPoller.DefaultPollIntervalSeconds;

    public const int MinPollIntervalSeconds = RecordingTargetPoller.MinPollIntervalSeconds;

    public const int MaxPollIntervalSeconds = RecordingTargetPoller.MaxPollIntervalSeconds;

    /// <summary>断流续录的最大重试次数，超过后交给下一轮开播轮询。</summary>
    public const int MaxRetryAttempts = 5;

    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(2);

    /// <summary>缺省保存目录：%UserProfile%\Videos\LimoClip。</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        DefaultRelativeDirectory);

    /// <summary>连续多少段都失败（启动即退出）后放弃自动续段，交给断流续录/开播轮询。</summary>
    public const int MaxConsecutivePartRestarts = RecordingSessionWorker.MaxConsecutivePartRestarts;

    private readonly IRoomRepository _rooms;
    private readonly IRecordingTargetRepository _targets;
    private readonly IRecordingSessionRepository _sessions;
    private readonly IAppSettingsRepository _settings;
    private readonly ICredentialStore _credentials;
    private readonly Func<PlatformType, IPlatformProvider?> _providerResolver;
    private readonly DanmakuManager? _danmaku;
    private readonly Func<IFullStreamRecorder> _recorderFactory;
    private readonly string? _globalDirectoryOverride;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;
    private readonly ProcessJobGuard _jobGuard;
    private readonly RecordingSessionWorkerContext _workerContext;
    private readonly RecordingTargetPoller _poller;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, RecordingSessionWorker> _active = new();
    private readonly Dictionary<Guid, RetryState> _retries = new();
    private readonly HashSet<Guid> _suppressedRooms = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private CancellationTokenSource? _lifetimeCts;
    private Task? _retryTask;
    private int _initialized;
    private int _disposed;

    public RecordingManager(
        IRoomRepository rooms,
        IRecordingTargetRepository targets,
        IRecordingSessionRepository sessions,
        IAppSettingsRepository settings,
        ICredentialStore credentials,
        Func<PlatformType, IPlatformProvider?> providerResolver,
        DanmakuManager? danmaku,
        Func<IFullStreamRecorder>? recorderFactory = null,
        string? globalDirectoryOverride = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null,
        TimeSpan? monitorInterval = null,
        TimeSpan? pollIntervalOverride = null,
        ISegmentMerger? segmentMerger = null)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _danmaku = danmaku;
        _globalDirectoryOverride = globalDirectoryOverride;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((timeSpan, token) => Task.Delay(timeSpan, token));
        _log = log ?? (_ => { });

        _jobGuard = new ProcessJobGuard();
        _recorderFactory = recorderFactory ?? CreateDefaultRecorder;
        _workerContext = new RecordingSessionWorkerContext(
            this,
            HighlightTracker,
            _danmaku,
            _recorderFactory,
            segmentMerger,
            _clock,
            _delay,
            monitorInterval ?? DefaultMonitorInterval);
        _poller = new RecordingTargetPoller(
            _rooms,
            _targets,
            _settings,
            _providerResolver,
            ReadCookieAsync,
            this,
            pollIntervalOverride,
            _delay);
    }

    /// <summary>
    /// 默认录制器工厂：定位随包 ffmpeg 与 cacert.crt。静态链接 ffmpeg 在部分环境无法注册
    /// Windows 证书库，传 <c>-ca_file</c> 兜底，避免 B 站/YouTube 等 HTTPS 拉流直接 TLS 失败。
    /// </summary>
    private IFullStreamRecorder CreateDefaultRecorder()
    {
        return new FfmpegFullRecorder(
            FfmpegLocator.Locate(),
            _jobGuard,
            caBundlePath: FfmpegLocator.LocateCaBundle());
    }

    /// <summary>高能事件绑定跟踪器；已挂接到 DanmakuManager 的 enricher 属性。</summary>
    public RecordingHighlightTracker HighlightTracker { get; } = new();

    /// <summary>当前开播轮询间隔。</summary>
    public TimeSpan PollInterval => _poller.PollInterval;

    /// <summary>录制进度更新；第一个参数为直播间主库 Id。</summary>
    public event Action<Guid, RecordingProgress>? ProgressUpdated;

    /// <summary>新的录制段开始（锚点前即触发，状态为 Recording）。</summary>
    public event Action<RecordingSession>? SessionStarted;

    /// <summary>录制段结束（Completed/Failed）。</summary>
    public event Action<RecordingSession>? SessionCompleted;

    /// <summary>运行期提示信息（自动起录、断流续录、失败等），供界面状态栏展示。</summary>
    public event Action<string>? StatusMessage;

    /// <summary>
    /// 初始化：崩溃恢复（把未收尾会话标记 Failed）、读取轮询间隔、挂接高能 enricher、启动轮询与续录循环。
    /// 重复调用是空操作。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        try
        {
            await RecoverUnfinishedSessionsAsync(ct).ConfigureAwait(false);
            await _poller.ConfigureAsync(ct).ConfigureAwait(false);

            if (_danmaku is not null)
            {
                _danmaku.HighlightEventEnricher = HighlightTracker;
            }

            _lifetimeCts = new CancellationTokenSource();
            var token = _lifetimeCts.Token;
            _poller.Start(token);
            _retryTask = Task.Run(() => RetryLoopAsync(token), token);
            _log($"录制引擎已启动：开播轮询间隔 {PollInterval.TotalSeconds:0} 秒，默认目录 {DefaultDirectory}。");
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>手动开始录制一个直播间；未开播或没有可用画质时抛异常。</summary>
    public async Task<RecordingSession> StartAsync(LiveRoom room, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.Id == Guid.Empty)
        {
            throw new ArgumentException("直播间 Id 不能为空。", nameof(room));
        }

        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException("录制引擎尚未初始化，请先调用 InitializeAsync。");
        }

        lock (_gate)
        {
            _suppressedRooms.Remove(room.Id);
        }

        var provider = ResolveProvider(room.Platform);
        var cookie = await ReadCookieAsync(room.Platform, ct).ConfigureAwait(false);
        var status = await provider.GetRoomStatusAsync(room.RoomId, cookie, ct).ConfigureAwait(false);
        return await StartCoreAsync(room, provider, cookie, status, ct).ConfigureAwait(false);
    }

    /// <summary>优雅停止指定直播间的录制；没有在录时是空操作。</summary>
    public async Task StopAsync(Guid liveRoomId, CancellationToken ct = default)
    {
        RecordingSessionWorker? worker;

        // 只把「查活动会话 + 置停止标记」放进 _startGate：启动窗口内（活动会话已登记、
        // recorder.StartAsync 尚未返回）若先收尾，StartCore 的后续代码会把已 Completed 的
        // 会话重新置为 Recording 并补发 SessionStarted，产生「Recording 但已写 EndedAt」的脏数据。
        // 拿到 _startGate 时启动流程必然已结束；耗时的优雅停止放在锁外，避免阻塞其它房间起录。
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _active.TryGetValue(liveRoomId, out worker);
            }

            if (worker is null)
            {
                return;
            }

            worker.StopRequested = true;
        }
        finally
        {
            _startGate.Release();
        }

        await SuppressAutoRestartAsync(worker.Room).ConfigureAwait(false);
        _log($"正在停止录制：{FormatRoom(worker.Room)}");

        var recorder = worker.Recorder;
        if (recorder is not null)
        {
            try
            {
                await recorder.StopGracefullyAsync(RecordingSessionWorker.GracefulStopTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"停止录制时 ffmpeg 收尾异常（{FormatRoom(worker.Room)}）：{ex.Message}");
            }
        }

        await worker.FinalizeAsync(requestedStop: true).ConfigureAwait(false);
    }

    /// <summary>设置直播间的自动录制与独立保存目录；开启后由开播轮询负责起录。</summary>
    public async Task SetAutoRecordAsync(
        Guid liveRoomId,
        bool enabled,
        string? customSaveDirectory,
        CancellationToken ct = default)
    {
        if (liveRoomId == Guid.Empty)
        {
            throw new ArgumentException("直播间 Id 不能为空。", nameof(liveRoomId));
        }

        var target = await _targets.GetAsync(liveRoomId, ct).ConfigureAwait(false)
                     ?? new RecordingTarget { LiveRoomId = liveRoomId };
        target.AutoRecordEnabled = enabled;
        target.CustomSaveDirectory = string.IsNullOrWhiteSpace(customSaveDirectory)
            ? null
            : customSaveDirectory.Trim();
        target.UpdatedAt = _clock().UtcDateTime;
        await _targets.UpsertAsync(target, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _retries.Remove(liveRoomId);
            if (!enabled)
            {
                _suppressedRooms.Remove(liveRoomId);
            }
        }
    }

    /// <summary>
    /// 执行一轮开播检查：读取主库与录制偏好，对所有开启自动录制且不在录的直播间查询开播状态并起录。
    /// 自动轮询循环也调用本方法。
    /// </summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException("录制引擎尚未初始化，请先调用 InitializeAsync。");
        }

        await _poller.PollOnceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>直播间当前是否正在录制。</summary>
    public bool IsRecording(Guid liveRoomId)
    {
        lock (_gate)
        {
            return _active.ContainsKey(liveRoomId);
        }
    }

    /// <summary>当前正在录制的会话 Id；未在录时为 null。</summary>
    public Guid? GetActiveSessionId(Guid liveRoomId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(liveRoomId, out var worker) ? worker.Session.Id : null;
        }
    }

    /// <summary>读取指定直播间的实时进度；未在录时为 null。</summary>
    public RecordingProgress? GetProgress(Guid liveRoomId)
    {
        RecordingSessionWorker? worker;
        lock (_gate)
        {
            _active.TryGetValue(liveRoomId, out worker);
        }

        return worker?.BuildProgress();
    }

    /// <summary>读取所有正在录制的房间快照（录制中心卡片数据源）。</summary>
    public IReadOnlyList<ActiveRecordingSnapshot> GetActiveRecordings()
    {
        List<RecordingSessionWorker> workers;
        lock (_gate)
        {
            workers = _active.Values.ToList();
        }

        var result = new List<ActiveRecordingSnapshot>(workers.Count);
        foreach (var worker in workers)
        {
            result.Add(new ActiveRecordingSnapshot(worker.Room, worker.BuildProgress()));
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<LiveRecordingInfo> GetLiveRecordings()
    {
        List<RecordingSessionWorker> workers;
        lock (_gate)
        {
            workers = _active.Values.ToList();
        }

        var result = new List<LiveRecordingInfo>(workers.Count);
        foreach (var worker in workers)
        {
            result.Add(worker.BuildLiveRecordingInfo());
        }

        return result;
    }

    /// <inheritdoc />
    public bool TryGetLiveRecording(Guid sessionId, out LiveRecordingInfo info)
    {
        List<RecordingSessionWorker> workers;
        lock (_gate)
        {
            workers = _active.Values.ToList();
        }

        foreach (var worker in workers)
        {
            if (worker.Session.Id == sessionId)
            {
                info = worker.BuildLiveRecordingInfo();
                return true;
            }
        }

        info = null!;
        return false;
    }

    /// <summary>
    /// 删除一条历史会话记录及其本地文件（先删文件，文件删除失败则保留记录并抛异常）。
    /// 正在录制的会话不允许删除。
    /// </summary>
    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _sessions.GetByIdAsync(sessionId, ct).ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"录制会话不存在：{sessionId}。");

        lock (_gate)
        {
            if (_active.Values.Any(worker => worker.Session.Id == sessionId))
            {
                throw new InvalidOperationException("该录制会话仍在进行中，请先停止录制再删除记录。");
            }
        }

        var failures = new List<string>();
        DeleteFileIfExists(session.VideoFilePath, failures);
        DeleteFileIfExists(session.DanmakuFilePath, failures);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"删除录制文件失败，记录已保留：{string.Join("；", failures)}");
        }

        await _sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        _log($"已删除录制记录及文件：{session.RoomDisplayName}（{session.VideoFilePath}）");
    }

    /// <summary>格式化直播间展示名（#序号 主播名），供界面与日志复用。</summary>
    public static string FormatRoomDisplayName(LiveRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var alias = string.IsNullOrWhiteSpace(room.Alias)
            ? $"{room.Platform}_{room.RoomId}"
            : room.Alias.Trim();
        return room.DisplayNumber > 0 ? $"#{room.DisplayNumber} {alias}" : alias;
    }

    /// <summary>停止所有录制、取消后台循环、卸载 enricher、释放 Job Object。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var cts = _lifetimeCts;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _poller.StopAsync().ConfigureAwait(false);

        if (_retryTask is not null)
        {
            try
            {
                await _retryTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 后台循环的异常已在内部记录，这里只等待退出。
            }
        }

        List<RecordingSessionWorker> workers;
        lock (_gate)
        {
            workers = _active.Values.ToList();
        }

        foreach (var worker in workers)
        {
            try
            {
                worker.StopRequested = true;
                var recorder = worker.Recorder;
                if (recorder is not null)
                {
                    await recorder.StopGracefullyAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log($"关闭时停止录制异常（{FormatRoom(worker.Room)}）：{ex.Message}");
            }

            try
            {
                await worker.FinalizeAsync(requestedStop: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"关闭时收尾录制异常（{FormatRoom(worker.Room)}）：{ex.Message}");
            }
        }

        if (_danmaku is not null && ReferenceEquals(_danmaku.HighlightEventEnricher, HighlightTracker))
        {
            _danmaku.HighlightEventEnricher = null;
        }

        cts?.Dispose();
        _startGate.Dispose();
        await _poller.DisposeAsync().ConfigureAwait(false);
        _jobGuard.Dispose();
        _log("录制引擎已停止。");
    }

    // ---- 开播轮询接缝（RecordingTargetPoller 的宿主实现，仅程序集内可见） ----

    bool IRecordingTargetPollerHost.IsRecording(Guid liveRoomId) => IsRecording(liveRoomId);

    bool IRecordingTargetPollerHost.IsAutoRestartSuppressed(Guid liveRoomId)
    {
        lock (_gate)
        {
            return _suppressedRooms.Contains(liveRoomId);
        }
    }

    void IRecordingTargetPollerHost.ResetRoomPollingState(Guid liveRoomId)
    {
        lock (_gate)
        {
            _suppressedRooms.Remove(liveRoomId);
            _retries.Remove(liveRoomId);
        }
    }

    Task IRecordingTargetPollerHost.StartAutoRecordingAsync(
        LiveRoom room,
        IPlatformProvider provider,
        string? cookie,
        RoomStatus status,
        CancellationToken ct)
        => StartCoreAsync(room, provider, cookie, status, ct);

    void IRecordingTargetPollerHost.Log(string message) => _log(message);

    void IRecordingTargetPollerHost.NotifyStatus(string message) => NotifyStatus(message);

    string IRecordingTargetPollerHost.FormatRoomDisplayName(LiveRoom room) => FormatRoomDisplayName(room);

    // ---- 录制会话接缝（RecordingSessionWorker 的宿主实现，仅程序集内可见） ----

    Task IRecordingSessionWorkerHost.PersistSessionAsync(RecordingSession session, string operation)
        => SafeUpdateSessionAsync(session, operation);

    void IRecordingSessionWorkerHost.RemoveActive(RecordingSessionWorker worker)
    {
        lock (_gate)
        {
            _active.Remove(worker.Room.Id);
        }
    }

    void IRecordingSessionWorkerHost.OnSessionFinalized(
        RecordingSessionWorker worker,
        RecordingProgress progress,
        bool userStop)
    {
        var session = worker.Session;

        // 先摘活动表再发事件：订阅者收到 SessionCompleted 时查询活动列表应已看不到该会话。
        lock (_gate)
        {
            _active.Remove(worker.Room.Id);
            if (userStop || session.DurationSeconds >= 30)
            {
                _retries.Remove(worker.Room.Id);
            }
        }

        RaiseSessionCompleted(session);
        RaiseProgress(worker.Room.Id, progress);
        _log($"录制结束：{session.RoomDisplayName}，状态 {session.Status}，时长 {session.DurationSeconds:0} 秒，文件 {session.FileSizeBytes / 1024.0 / 1024.0:0.0} MB，弹幕 {session.DanmakuCount} 条，高能 {session.HighlightCount} 次。");
        NotifyStatus(userStop
            ? $"已停止录制：{session.RoomDisplayName}"
            : $"录制中断：{session.RoomDisplayName}（{session.Status}）");
    }

    void IRecordingSessionWorkerHost.OnSessionFinalizeFailed(RecordingSessionWorker worker, Exception exception)
    {
        lock (_gate)
        {
            _active.Remove(worker.Room.Id);
        }

        _log($"录制收尾失败（{worker.Session.RoomDisplayName}）：{exception.Message}");
    }

    void IRecordingSessionWorkerHost.OnProgress(RecordingSessionWorker worker, RecordingProgress progress)
        => RaiseProgress(worker.Room.Id, progress);

    void IRecordingSessionWorkerHost.Log(string message) => _log(message);

    void IRecordingSessionWorkerHost.NotifyStatus(string message) => NotifyStatus(message);

    Task<bool> IRecordingSessionWorkerHost.IsRoomOfflineAsync(LiveRoom room) => IsRoomOfflineAsync(room);

    Task IRecordingSessionWorkerHost.MaybeScheduleRetryAsync(LiveRoom room) => MaybeScheduleRetryAsync(room);

    // ---- 启动 ----

    private async Task<RecordingSession> StartCoreAsync(
        LiveRoom room,
        IPlatformProvider provider,
        string? cookie,
        RoomStatus status,
        CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRecording(room.Id))
            {
                throw new InvalidOperationException($"直播间「{FormatRoomDisplayName(room)}」正在录制中。");
            }

            if (!status.IsLive)
            {
                throw new InvalidOperationException($"主播未开播，无法开始录制：{FormatRoomDisplayName(room)}。");
            }

            var quality = RecordingSessionWorker.ChooseQuality(status)
                          ?? throw new InvalidOperationException($"直播间没有可用的清晰度：{FormatRoomDisplayName(room)}。");

            var target = await _targets.GetAsync(room.Id, ct).ConfigureAwait(false);
            var outputDirectory = await ResolveOutputDirectoryAsync(target, ct).ConfigureAwait(false);

            // 开录前先验证目录可写：避免先落库会话、几秒后 ffmpeg 才因权限失败。
            RecordingDirectoryGuard.EnsureWritable(outputDirectory);

            var startedAtUtc = _clock();
            var baseName = RecordingFileNaming.BuildBaseName(room, startedAtUtc);
            var videoPath = Path.Combine(outputDirectory, baseName + ".mkv");
            // V0.6-A 自定义清流没有弹幕服务：不创建 .jsonl 伴随文件，剪辑台按空路径自动跳过弹幕轨。
            var danmakuPath = room.Platform == PlatformType.CustomStream
                ? string.Empty
                : Path.Combine(outputDirectory, baseName + ".jsonl");

            Directory.CreateDirectory(outputDirectory);

            var session = new RecordingSession
            {
                Id = Guid.NewGuid(),
                LiveRoomId = room.Id,
                Platform = room.Platform,
                RoomId = room.RoomId.Trim(),
                RoomDisplayName = FormatRoomDisplayName(room),
                StreamTitle = status.Title?.Trim() ?? string.Empty,
                Status = RecordingStatus.Starting,
                VideoFilePath = videoPath,
                DanmakuFilePath = danmakuPath,
                StartedAt = startedAtUtc.UtcDateTime,
            };

            await _sessions.AddAsync(session, ct).ConfigureAwait(false);

            RecordingSessionWorker? worker = null;
            try
            {
                var recorder = _recorderFactory();
                worker = new RecordingSessionWorker(
                    room,
                    session,
                    recorder,
                    provider,
                    cookie,
                    quality,
                    startedAtUtc,
                    _workerContext);

                // 活动表在录制器真正起流之前登记：启动窗口内 IsRecording 必须为 true，
                // 手动停止要能定位到本会话（启动失败时由 CleanupStartFailureAsync 摘除）。
                lock (_gate)
                {
                    _active[room.Id] = worker;
                }

                await worker.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (worker is not null)
                {
                    await worker.CleanupStartFailureAsync(ex).ConfigureAwait(false);
                }
                else
                {
                    session.Status = RecordingStatus.Failed;
                    session.EndedAt = _clock().UtcDateTime;
                    session.ErrorMessage = $"创建录制器失败：{ex.Message}";
                    await SafeUpdateSessionAsync(session, "标记录制启动失败").ConfigureAwait(false);
                }

                throw;
            }

            session.Status = RecordingStatus.Recording;
            await SafeUpdateSessionAsync(session, "更新录制开始状态").ConfigureAwait(false);

            worker.StartMonitoring();
            RaiseSessionStarted(session);
            RaiseProgress(room.Id, worker.BuildProgress());
            _log($"开始录制：{session.RoomDisplayName} → {videoPath}（{quality.DisplayName}）");
            NotifyStatus($"开始录制：{session.RoomDisplayName}");
            return session;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task SuppressAutoRestartAsync(LiveRoom room)
    {
        try
        {
            var target = await _targets.GetAsync(room.Id, CancellationToken.None).ConfigureAwait(false);
            if (target is { AutoRecordEnabled: true })
            {
                lock (_gate)
                {
                    _suppressedRooms.Add(room.Id);
                    _retries.Remove(room.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"读取录制偏好失败（{FormatRoom(room)}）：{ex.Message}");
        }
    }

    private async Task MaybeScheduleRetryAsync(LiveRoom room)
    {
        RecordingTarget? target;
        try
        {
            target = await _targets.GetAsync(room.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"读取录制偏好失败，跳过断流续录（{FormatRoom(room)}）：{ex.Message}");
            return;
        }

        if (target is not { AutoRecordEnabled: true })
        {
            return;
        }

        int attempt;
        lock (_gate)
        {
            if (_suppressedRooms.Contains(room.Id))
            {
                return;
            }

            var previous = _retries.TryGetValue(room.Id, out var existing) ? existing.Attempts : 0;
            attempt = previous + 1;
            if (attempt > MaxRetryAttempts)
            {
                _retries.Remove(room.Id);
                _log($"断流续录已达上限（{MaxRetryAttempts} 次），等待下一轮开播轮询：{FormatRoom(room)}");
                return;
            }

            _retries[room.Id] = new RetryState
            {
                Attempts = attempt,
                NextAttemptUtc = _clock() + BackoffFor(attempt),
            };
        }

        _log($"检测到录制中断，{BackoffFor(attempt).TotalSeconds:0} 秒后尝试续录（第 {attempt} 次）：{FormatRoom(room)}");
        NotifyStatus($"录制中断，准备续录：{FormatRoom(room)}");
    }

    // ---- 后台循环 ----

    private async Task RetryLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _delay(RetryLoopInterval, token).ConfigureAwait(false);
                try
                {
                    await ProcessDueRetriesAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"断流续录循环异常：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // 断流续录的重试节拍常量（仅管理器层使用）。
    private static readonly TimeSpan RetryLoopInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(60);

    private async Task ProcessDueRetriesAsync(CancellationToken ct)
    {
        List<Guid> due;
        lock (_gate)
        {
            var now = _clock();
            due = _retries
                .Where(pair => now >= pair.Value.NextAttemptUtc)
                .Select(pair => pair.Key)
                .ToList();
        }

        foreach (var roomId in due)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            LiveRoom? room;
            try
            {
                room = await _rooms.GetByIdAsync(roomId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"读取直播间失败，跳过本轮续录（{roomId}）：{ex.Message}");
                continue;
            }

            if (room is null || IsRecording(roomId))
            {
                lock (_gate)
                {
                    _retries.Remove(roomId);
                }

                continue;
            }

            try
            {
                await StartAsync(room, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _retries.Remove(roomId);
                }

                _log($"断流续录成功：{FormatRoom(room)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_retries.TryGetValue(roomId, out var state))
                    {
                        state.Attempts++;
                        state.LastError = ex.Message;
                        if (state.Attempts > MaxRetryAttempts)
                        {
                            _retries.Remove(roomId);
                        }
                        else
                        {
                            state.NextAttemptUtc = _clock() + BackoffFor(state.Attempts);
                        }
                    }
                }

                _log($"断流续录失败（{FormatRoom(room)}）：{ex.Message}");
            }
        }
    }

    // ---- 辅助 ----

    private async Task RecoverUnfinishedSessionsAsync(CancellationToken ct)
    {
        IReadOnlyList<RecordingSession> unfinished;
        try
        {
            unfinished = await _sessions.GetUnfinishedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"读取未收尾录制会话失败：{ex.Message}");
            return;
        }

        if (unfinished.Count == 0)
        {
            return;
        }

        foreach (var session in unfinished)
        {
            session.Status = RecordingStatus.Failed;
            session.EndedAt = _clock().UtcDateTime;
            session.ErrorMessage = "程序异常退出，录制未正常收尾，文件可能不完整。";
            var fileSize = RecordingSessionWorker.TryGetFileLength(session.VideoFilePath);
            if (fileSize > session.FileSizeBytes)
            {
                session.FileSizeBytes = fileSize;
            }

            await SafeUpdateSessionAsync(session, "崩溃恢复标记").ConfigureAwait(false);
        }

        _log($"崩溃恢复：已把 {unfinished.Count} 个未收尾的录制会话标记为 Failed。");
    }

    private async Task<string> ResolveOutputDirectoryAsync(RecordingTarget? target, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(target?.CustomSaveDirectory))
        {
            return target!.CustomSaveDirectory!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_globalDirectoryOverride))
        {
            return _globalDirectoryOverride!;
        }

        try
        {
            var configured = await _settings.GetAsync(AppSettingKeys.RecordingDefaultDirectory, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"读取默认录制目录失败，使用缺省值：{ex.Message}");
        }

        return DefaultDirectory;
    }

    private IPlatformProvider ResolveProvider(PlatformType platform)
        => _providerResolver(platform)
           ?? throw new NotSupportedException($"未注册平台 {platform} 的 Provider。");

    private async Task<string?> ReadCookieAsync(PlatformType platform, CancellationToken ct)
    {
        try
        {
            return await _credentials.GetCookieAsync(platform, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"读取平台 Cookie 失败（{platform}），将以游客态录制：{ex.Message}");
            return null;
        }
    }

    private async Task<bool> IsRoomOfflineAsync(LiveRoom room)
    {
        var provider = _providerResolver(room.Platform);
        if (provider is null)
        {
            return false;
        }

        try
        {
            var cookie = await ReadCookieAsync(room.Platform, CancellationToken.None).ConfigureAwait(false);
            var status = await provider.GetRoomStatusAsync(room.RoomId, cookie, CancellationToken.None).ConfigureAwait(false);
            return !status.IsLive;
        }
        catch (Exception ex)
        {
            _log($"收尾时查询开播状态失败（{FormatRoom(room)}）：{ex.Message}");
            return false;
        }
    }

    private async Task SafeUpdateSessionAsync(RecordingSession session, string operation)
    {
        try
        {
            await _sessions.UpdateAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"{operation}失败（{session.RoomDisplayName}）：{ex.Message}");
        }
    }

    private void RaiseProgress(Guid liveRoomId, RecordingProgress progress)
    {
        try
        {
            ProgressUpdated?.Invoke(liveRoomId, progress);
        }
        catch (Exception ex)
        {
            _log($"录制进度订阅者异常：{ex.Message}");
        }
    }

    private void RaiseSessionStarted(RecordingSession session)
    {
        try
        {
            SessionStarted?.Invoke(session);
        }
        catch (Exception ex)
        {
            _log($"录制开始订阅者异常：{ex.Message}");
        }
    }

    private void RaiseSessionCompleted(RecordingSession session)
    {
        try
        {
            SessionCompleted?.Invoke(session);
        }
        catch (Exception ex)
        {
            _log($"录制结束订阅者异常：{ex.Message}");
        }
    }

    private void NotifyStatus(string message)
    {
        try
        {
            StatusMessage?.Invoke(message);
        }
        catch (Exception)
        {
            // 界面提示失败不影响录制。
        }
    }

    private void DeleteFileIfExists(string? path, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{path}（{ex.Message}）");
            _log($"删除录制文件失败（{path}）：{ex.Message}");
        }
    }

    private static string FormatRoom(LiveRoom room) => FormatRoomDisplayName(room);

    private static TimeSpan BackoffFor(int attempt)
    {
        var seconds = Math.Min(
            RetryMaxDelay.TotalSeconds,
            RetryBaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class RetryState
    {
        public int Attempts { get; set; }

        public DateTimeOffset NextAttemptUtc { get; set; }

        public string? LastError { get; set; }
    }
}
