using System.Globalization;
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
/// 依赖 <see cref="DanmakuManager"/> 共享同房间的弹幕连接（监控与录制同房间只有一条 WebSocket）；
/// DanmakuManager 可以为 null（例如无弹幕场景），此时不写弹幕伴随文件。
/// </para>
/// </summary>
public sealed class RecordingManager : IAsyncDisposable
{
    /// <summary>缺省保存目录的末尾相对路径，拼接在用户目录下。</summary>
    public const string DefaultRelativeDirectory = @"Videos\LimoClip";

    /// <summary>缺省开播轮询间隔（秒）。</summary>
    public const int DefaultPollIntervalSeconds = 60;

    public const int MinPollIntervalSeconds = 10;

    public const int MaxPollIntervalSeconds = 600;

    /// <summary>断流续录的最大重试次数，超过后交给下一轮开播轮询。</summary>
    public const int MaxRetryAttempts = 5;

    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryLoopInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromSeconds(3);

    /// <summary>缺省保存目录：%UserProfile%\Videos\LimoClip。</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        DefaultRelativeDirectory);

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
    private readonly TimeSpan _monitorInterval;
    private readonly TimeSpan? _pollIntervalOverride;
    private readonly ProcessJobGuard _jobGuard;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveRecording> _active = new();
    private readonly Dictionary<Guid, RetryState> _retries = new();
    private readonly HashSet<Guid> _suppressedRooms = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _pollGate = new(4, 4);

    private CancellationTokenSource? _lifetimeCts;
    private Task? _pollTask;
    private Task? _retryTask;
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
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
        TimeSpan? pollIntervalOverride = null)
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
        _monitorInterval = monitorInterval ?? DefaultMonitorInterval;
        _pollIntervalOverride = pollIntervalOverride;
        if (_pollIntervalOverride is { } pollInterval)
        {
            _pollInterval = ClampPollInterval(pollInterval);
        }

        _jobGuard = new ProcessJobGuard();
        _recorderFactory = recorderFactory ?? CreateDefaultRecorder;
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
    public TimeSpan PollInterval
    {
        get
        {
            lock (_gate)
            {
                return _pollInterval;
            }
        }
    }

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

            if (_pollIntervalOverride is null)
            {
                var interval = await ReadPollIntervalAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _pollInterval = interval;
                }
            }

            if (_danmaku is not null)
            {
                _danmaku.HighlightEventEnricher = HighlightTracker;
            }

            _lifetimeCts = new CancellationTokenSource();
            var token = _lifetimeCts.Token;
            _pollTask = Task.Run(() => PollLoopAsync(token), token);
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
        ActiveRecording? active;
        lock (_gate)
        {
            _active.TryGetValue(liveRoomId, out active);
        }

        if (active is null)
        {
            return;
        }

        active.StopRequested = true;
        await SuppressAutoRestartAsync(active.Room).ConfigureAwait(false);
        _log($"正在停止录制：{FormatRoom(active.Room)}");

        try
        {
            await active.Recorder.StopGracefullyAsync(GracefulStopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"停止录制时 ffmpeg 收尾异常（{FormatRoom(active.Room)}）：{ex.Message}");
        }

        await FinalizeOnceAsync(active, requestedStop: true).ConfigureAwait(false);
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
            _log($"读取直播间/录制偏好失败，本轮开播检查跳过：{ex.Message}");
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

            if (IsRecording(room.Id))
            {
                continue;
            }

            tasks.Add(PollRoomAsync(room, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
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
            return _active.TryGetValue(liveRoomId, out var active) ? active.Session.Id : null;
        }
    }

    /// <summary>读取指定直播间的实时进度；未在录时为 null。</summary>
    public RecordingProgress? GetProgress(Guid liveRoomId)
    {
        ActiveRecording? active;
        lock (_gate)
        {
            _active.TryGetValue(liveRoomId, out active);
        }

        return active is null ? null : BuildProgress(active);
    }

    /// <summary>读取所有正在录制的房间快照（录制中心卡片数据源）。</summary>
    public IReadOnlyList<ActiveRecordingSnapshot> GetActiveRecordings()
    {
        List<ActiveRecording> actives;
        lock (_gate)
        {
            actives = _active.Values.ToList();
        }

        var result = new List<ActiveRecordingSnapshot>(actives.Count);
        foreach (var active in actives)
        {
            result.Add(new ActiveRecordingSnapshot(active.Room, BuildProgress(active)));
        }

        return result;
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
            if (_active.Values.Any(active => active.Session.Id == sessionId))
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

        foreach (var task in new[] { _pollTask, _retryTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 后台循环的异常已在内部记录，这里只等待退出。
            }
        }

        List<ActiveRecording> actives;
        lock (_gate)
        {
            actives = _active.Values.ToList();
        }

        foreach (var active in actives)
        {
            try
            {
                active.StopRequested = true;
                await active.Recorder.StopGracefullyAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"关闭时停止录制异常（{FormatRoom(active.Room)}）：{ex.Message}");
            }

            try
            {
                await FinalizeOnceAsync(active, requestedStop: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"关闭时收尾录制异常（{FormatRoom(active.Room)}）：{ex.Message}");
            }
        }

        if (_danmaku is not null && ReferenceEquals(_danmaku.HighlightEventEnricher, HighlightTracker))
        {
            _danmaku.HighlightEventEnricher = null;
        }

        cts?.Dispose();
        _startGate.Dispose();
        _pollGate.Dispose();
        _jobGuard.Dispose();
        _log("录制引擎已停止。");
    }

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

            var quality = ChooseQuality(status)
                          ?? throw new InvalidOperationException($"直播间没有可用的清晰度：{FormatRoomDisplayName(room)}。");

            var target = await _targets.GetAsync(room.Id, ct).ConfigureAwait(false);
            var outputDirectory = await ResolveOutputDirectoryAsync(target, ct).ConfigureAwait(false);
            var startedAtUtc = _clock();
            var baseName = RecordingFileNaming.BuildBaseName(room, startedAtUtc);
            var videoPath = Path.Combine(outputDirectory, baseName + ".mkv");
            var danmakuPath = Path.Combine(outputDirectory, baseName + ".jsonl");

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

            ActiveRecording? active = null;
            try
            {
                active = new ActiveRecording(room, session, _recorderFactory(), startedAtUtc);

                try
                {
                    active.Jsonl = new DanmakuJsonlRecorder(
                        danmakuPath,
                        session.Id,
                        room.Platform,
                        session.RoomId,
                        _log);
                }
                catch (Exception ex)
                {
                    _log($"弹幕伴随文件创建失败，本次录制不写 .jsonl（{session.RoomDisplayName}）：{ex.Message}");
                }

                AttachDanmaku(active);
                HighlightTracker.Register(room.Platform, session.RoomId, session.Id);
                SubscribeRecorder(active);

                lock (_gate)
                {
                    _active[room.Id] = active;
                }

                await active.Recorder.StartAsync(quality, videoPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (active is not null)
                {
                    await CleanupStartFailureAsync(active, ex).ConfigureAwait(false);
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

            active.MonitorTask = Task.Run(() => MonitorAsync(active), CancellationToken.None);
            RaiseSessionStarted(session);
            RaiseProgress(room.Id, BuildProgress(active));
            _log($"开始录制：{session.RoomDisplayName} → {videoPath}（{quality.DisplayName}）");
            NotifyStatus($"开始录制：{session.RoomDisplayName}");
            return session;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task CleanupStartFailureAsync(ActiveRecording active, Exception startError)
    {
        DetachDanmaku(active);
        HighlightTracker.Remove(active.Room.Platform, active.Session.RoomId);
        lock (_gate)
        {
            _active.Remove(active.Room.Id);
        }

        if (active.Jsonl is not null)
        {
            try
            {
                await active.Jsonl.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"启动失败清理弹幕文件异常：{ex.Message}");
            }

            active.Jsonl = null;
        }

        try
        {
            await active.Recorder.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"启动失败清理录制器异常：{ex.Message}");
        }

        try
        {
            active.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        active.Cts.Dispose();

        // 启动失败的分段没有保留价值：清掉可能的半成品文件，并把会话标记为 Failed 供历史列表展示。
        TryDeleteQuietly(active.Session.VideoFilePath);
        TryDeleteQuietly(active.Session.DanmakuFilePath);
        active.Session.Status = RecordingStatus.Failed;
        active.Session.EndedAt = _clock().UtcDateTime;
        active.Session.ErrorMessage = $"启动录制失败：{startError.Message}";
        await SafeUpdateSessionAsync(active.Session, "标记录制启动失败").ConfigureAwait(false);
    }

    private void TryDeleteQuietly(string? path)
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
            _log($"清理启动失败产物异常（{path}）：{ex.Message}");
        }
    }

    private void SubscribeRecorder(ActiveRecording active)
    {
        active.Recorder.OnFirstFrameAnchored += anchor => OnAnchor(active, anchor);
        active.Recorder.OnProgressUpdated += (duration, size, bitrate) => OnProgress(active, duration, size, bitrate);
        active.Recorder.OnExited += (exitCode, stderrTail) => OnRecorderExited(active, exitCode, stderrTail);
    }

    // ---- 采集 ----

    private void AttachDanmaku(ActiveRecording active)
    {
        if (_danmaku is null)
        {
            return;
        }

        try
        {
            var handle = _danmaku.Acquire(active.Room.Platform, active.Room.RoomId);
            active.Handle = handle;
            handle.MessagesReceived += batch => OnDanmakuBatch(active, batch);
        }
        catch (Exception ex)
        {
            _log($"弹幕订阅失败，本次录制不写弹幕伴随文件（{FormatRoom(active.Room)}）：{ex.Message}");
        }
    }

    private void DetachDanmaku(ActiveRecording active)
    {
        var handle = active.Handle;
        active.Handle = null;
        if (handle is null)
        {
            return;
        }

        try
        {
            handle.Dispose();
        }
        catch (Exception ex)
        {
            _log($"释放弹幕句柄异常：{ex.Message}");
        }
    }

    private void OnDanmakuBatch(ActiveRecording active, IReadOnlyList<DanmakuMessage> batch)
    {
        var jsonl = active.Jsonl;
        if (jsonl is null)
        {
            return;
        }

        var written = 0;
        foreach (var message in batch)
        {
            if (jsonl.Enqueue(message))
            {
                written++;
            }
        }

        if (written > 0)
        {
            Interlocked.Add(ref active.DanmakuCount, written);
        }
    }

    private void OnAnchor(ActiveRecording active, DateTimeOffset anchorUtc)
    {
        lock (active.Sync)
        {
            if (active.AnchorUtc is null)
            {
                active.AnchorUtc = anchorUtc;
                active.Session.StartedAt = anchorUtc.UtcDateTime;
                active.LastProgressUtc = anchorUtc;
            }
        }

        HighlightTracker.UpdateAnchor(active.Session.Id, anchorUtc);
        try
        {
            active.Jsonl?.UpdateAnchor(anchorUtc);
        }
        catch (Exception ex)
        {
            _log($"校准弹幕文件锚点异常：{ex.Message}");
        }
    }

    private void OnProgress(ActiveRecording active, TimeSpan duration, long fileSizeBytes, double bitrateKbps)
    {
        lock (active.Sync)
        {
            active.LastDuration = duration;
            active.LastSize = fileSizeBytes;
            active.LastBitrate = bitrateKbps;
            active.LastProgressUtc = _clock();
            active.Session.DurationSeconds = Math.Max(active.Session.DurationSeconds, duration.TotalSeconds);
            active.Session.FileSizeBytes = Math.Max(active.Session.FileSizeBytes, fileSizeBytes);
        }

        RaiseProgress(active.Room.Id, BuildProgress(active));
    }

    private void OnRecorderExited(ActiveRecording active, int exitCode, string? stderrTail)
    {
        lock (active.Sync)
        {
            active.RecorderExited = true;
            active.ExitCode = exitCode;
            active.StderrTail = stderrTail;
        }
    }

    // ---- 监控循环 ----

    private async Task MonitorAsync(ActiveRecording active)
    {
        CancellationToken token;
        try
        {
            token = active.Cts.Token;
        }
        catch (ObjectDisposedException)
        {
            // 启动失败清理路径可能已释放 Cts，此时无需再监控。
            return;
        }

        var nextPersist = _clock() + PersistInterval;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _delay(_monitorInterval, token).ConfigureAwait(false);
                if (active.RecorderExited || active.StopRequested)
                {
                    break;
                }

                DateTimeOffset lastProgress;
                lock (active.Sync)
                {
                    lastProgress = active.LastProgressUtc;
                }

                var now = _clock();
                if (lastProgress != default && now - lastProgress > StallTimeout)
                {
                    active.StallDetected = true;
                    _log($"录制进度超过 {StallTimeout.TotalSeconds:0} 秒无更新，判定流卡死，终止当前分段：{active.Session.RoomDisplayName}");
                    if (active.Jsonl is not null)
                    {
                        try
                        {
                            await active.Jsonl.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log($"卡死收尾弹幕文件异常：{ex.Message}");
                        }

                        active.Jsonl = null;
                    }

                    try
                    {
                        await active.Recorder.StopGracefullyAsync(GracefulStopTimeout).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log($"卡死终止录制器异常：{ex.Message}");
                    }

                    break;
                }

                if (now >= nextPersist)
                {
                    nextPersist = now + PersistInterval;
                    await PersistActiveAsync(active).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 管理器停止/收尾时取消，正常退出。
        }
        catch (Exception ex)
        {
            _log($"录制监控循环异常（{FormatRoom(active.Room)}）：{ex.Message}");
        }

        try
        {
            if (!active.StopRequested && !token.IsCancellationRequested)
            {
                try
                {
                    await FinalizeOnceAsync(active, requestedStop: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"录制自动收尾异常（{FormatRoom(active.Room)}）：{ex.Message}");
                }
            }
        }
        finally
        {
            // Cts 只在监控循环退出时释放，避免与收尾路径的 Cancel 竞态。
            try
            {
                active.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task PersistActiveAsync(ActiveRecording active)
    {
        RecordingSession session = active.Session;
        lock (active.Sync)
        {
            session.DurationSeconds = Math.Max(session.DurationSeconds, active.LastDuration.TotalSeconds);
            session.FileSizeBytes = Math.Max(session.FileSizeBytes, active.LastSize);
            session.DanmakuCount = Math.Max(session.DanmakuCount, Volatile.Read(ref active.DanmakuCount));
            session.HighlightCount = Math.Max(
                session.HighlightCount,
                HighlightTracker.GetHighlightCount(session.Id));
        }

        await SafeUpdateSessionAsync(session, "录制进度落盘").ConfigureAwait(false);
    }

    // ---- 收尾 ----

    private async Task<RecordingSession> FinalizeOnceAsync(ActiveRecording active, bool requestedStop)
    {
        if (Interlocked.Exchange(ref active.FinalizeStarted, 1) != 0)
        {
            return await active.FinalizeTcs.Task.ConfigureAwait(false);
        }

        var session = active.Session;
        var userStop = requestedStop || active.StopRequested;
        try
        {
            try
            {
                active.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            DetachDanmaku(active);
            var jsonl = active.Jsonl;
            active.Jsonl = null;
            if (jsonl is not null)
            {
                try
                {
                    await jsonl.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"收尾弹幕文件异常（{session.RoomDisplayName}）：{ex.Message}");
                }
            }

            try
            {
                await active.Recorder.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"收尾录制器异常（{session.RoomDisplayName}）：{ex.Message}");
            }

            var endedAt = _clock();
            var highlightCount = HighlightTracker.Remove(active.Room.Platform, session.RoomId);
            var fileSize = TryGetFileLength(session.VideoFilePath);
            var written = jsonl?.WrittenCount ?? 0;

            // 开播状态查询是异步 IO，必须在 lock 外完成。
            var offline = false;
            if (!userStop && !active.StallDetected)
            {
                try
                {
                    offline = await IsRoomOfflineAsync(active.Room).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"收尾时确认开播状态失败（{FormatRoom(active.Room)}）：{ex.Message}");
                }
            }

            RecordingProgress progress;
            lock (active.Sync)
            {
                var durationAnchor = active.AnchorUtc ?? active.RecordingStartUtc;
                session.EndedAt = endedAt.UtcDateTime;
                session.DurationSeconds = Math.Max(session.DurationSeconds, active.LastDuration.TotalSeconds);
                session.DurationSeconds = Math.Max(
                    session.DurationSeconds,
                    Math.Max(0, (endedAt - durationAnchor).TotalSeconds));
                session.FileSizeBytes = Math.Max(session.FileSizeBytes, Math.Max(active.LastSize, fileSize));
                session.DanmakuCount = Math.Max(session.DanmakuCount, Math.Max(written, Volatile.Read(ref active.DanmakuCount)));
                session.HighlightCount = Math.Max(session.HighlightCount, highlightCount);

                if (userStop)
                {
                    session.Status = RecordingStatus.Completed;
                    session.ErrorMessage = null;
                }
                else if (active.StallDetected)
                {
                    session.Status = RecordingStatus.Failed;
                    session.ErrorMessage = $"录制流卡死超过 {StallTimeout.TotalSeconds:0} 秒，已强制结束当前分段。";
                }
                else
                {
                    if (offline)
                    {
                        session.Status = RecordingStatus.Completed;
                        session.ErrorMessage = null;
                    }
                    else
                    {
                        session.Status = RecordingStatus.Failed;
                        session.ErrorMessage = DescribeExit(active);
                    }
                }

                progress = new RecordingProgress(
                    session.Id,
                    TimeSpan.FromSeconds(session.DurationSeconds),
                    session.FileSizeBytes,
                    active.LastBitrate,
                    session.DanmakuCount,
                    session.HighlightCount);
            }

            await SafeUpdateSessionAsync(session, "录制收尾落盘").ConfigureAwait(false);

            lock (_gate)
            {
                _active.Remove(active.Room.Id);
                if (userStop || session.DurationSeconds >= 30)
                {
                    _retries.Remove(active.Room.Id);
                }
            }

            RaiseSessionCompleted(session);
            RaiseProgress(active.Room.Id, progress);
            _log($"录制结束：{session.RoomDisplayName}，状态 {session.Status}，时长 {session.DurationSeconds:0} 秒，文件 {session.FileSizeBytes / 1024.0 / 1024.0:0.0} MB，弹幕 {session.DanmakuCount} 条，高能 {session.HighlightCount} 次。");
            NotifyStatus(userStop
                ? $"已停止录制：{session.RoomDisplayName}"
                : $"录制中断：{session.RoomDisplayName}（{session.Status}）");

            active.FinalizeTcs.TrySetResult(session);

            if (!userStop)
            {
                await MaybeScheduleRetryAsync(active.Room).ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception ex)
        {
            active.FinalizeTcs.TrySetException(ex);
            lock (_gate)
            {
                _active.Remove(active.Room.Id);
            }

            _log($"录制收尾失败（{session.RoomDisplayName}）：{ex.Message}");
            throw;
        }
        finally
        {
            // Cts 由监控循环退出时释放（见 MonitorAsync 的 finally）。
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
                    _log($"开播轮询失败：{ex.Message}");
                }

                await _delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

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

    private async Task PollRoomAsync(LiveRoom room, CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRecording(room.Id))
            {
                return;
            }

            var provider = _providerResolver(room.Platform);
            if (provider is null)
            {
                _log($"未注册平台 {room.Platform} 的 Provider，跳过自动录制检查。");
                return;
            }

            var cookie = await ReadCookieAsync(room.Platform, ct).ConfigureAwait(false);
            var status = await provider.GetRoomStatusAsync(room.RoomId, cookie, ct).ConfigureAwait(false);
            if (!status.IsLive)
            {
                lock (_gate)
                {
                    _suppressedRooms.Remove(room.Id);
                    _retries.Remove(room.Id);
                }

                return;
            }

            lock (_gate)
            {
                if (_suppressedRooms.Contains(room.Id))
                {
                    return;
                }
            }

            await StartCoreAsync(room, provider, cookie, status, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log($"自动录制检查失败（{FormatRoom(room)}）：{ex.Message}");
        }
        finally
        {
            _pollGate.Release();
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
            var fileSize = TryGetFileLength(session.VideoFilePath);
            if (fileSize > session.FileSizeBytes)
            {
                session.FileSizeBytes = fileSize;
            }

            await SafeUpdateSessionAsync(session, "崩溃恢复标记").ConfigureAwait(false);
        }

        _log($"崩溃恢复：已把 {unfinished.Count} 个未收尾的录制会话标记为 Failed。");
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
            _log($"读取录制轮询间隔失败，使用默认值：{ex.Message}");
        }

        return TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
    }

    private static TimeSpan ClampPollInterval(TimeSpan value)
    {
        var seconds = (int)Math.Round(value.TotalSeconds);
        seconds = Math.Clamp(seconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
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

    private static StreamQuality? ChooseQuality(RoomStatus status)
        => status.AvailableQualities
            .Where(quality => !string.IsNullOrWhiteSpace(quality.StreamUrl))
            .OrderByDescending(quality => quality.Height)
            .ThenByDescending(quality => quality.Fps)
            .FirstOrDefault();

    private static string DescribeExit(ActiveRecording active)
    {
        var stderr = string.IsNullOrWhiteSpace(active.StderrTail)
            ? "无 stderr 输出"
            : active.StderrTail!.Trim();
        if (stderr.Length > 300)
        {
            stderr = stderr[^300..];
        }

        return $"ffmpeg 异常退出（exitCode={active.ExitCode}）：{stderr}";
    }

    private RecordingProgress BuildProgress(ActiveRecording active)
    {
        lock (active.Sync)
        {
            return new RecordingProgress(
                active.Session.Id,
                active.LastDuration > TimeSpan.Zero
                    ? active.LastDuration
                    : TimeSpan.FromSeconds(Math.Max(0, (_clock() - (active.AnchorUtc ?? active.RecordingStartUtc)).TotalSeconds)),
                Math.Max(active.LastSize, active.Session.FileSizeBytes),
                active.LastBitrate,
                Math.Max(active.Session.DanmakuCount, Volatile.Read(ref active.DanmakuCount)),
                Math.Max(active.Session.HighlightCount, HighlightTracker.GetHighlightCount(active.Session.Id)));
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

    private static long TryGetFileLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
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

    private sealed class ActiveRecording
    {
        public ActiveRecording(
            LiveRoom room,
            RecordingSession session,
            IFullStreamRecorder recorder,
            DateTimeOffset recordingStartUtc)
        {
            Room = room;
            Session = session;
            Recorder = recorder;
            RecordingStartUtc = recordingStartUtc;
            LastProgressUtc = recordingStartUtc;
        }

        public LiveRoom Room { get; }

        public RecordingSession Session { get; }

        public IFullStreamRecorder Recorder { get; }

        public DateTimeOffset RecordingStartUtc { get; }

        public DanmakuJsonlRecorder? Jsonl { get; set; }

        public DanmakuSessionHandle? Handle { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public object Sync { get; } = new();

        public bool StopRequested { get; set; }

        public bool StallDetected { get; set; }

        public bool RecorderExited { get; set; }

        public int ExitCode { get; set; }

        public string? StderrTail { get; set; }

        public DateTimeOffset? AnchorUtc { get; set; }

        public DateTimeOffset LastProgressUtc { get; set; }

        public TimeSpan LastDuration { get; set; }

        public long LastSize { get; set; }

        public double LastBitrate { get; set; }

        public int DanmakuCount;

        public int FinalizeStarted;

        public TaskCompletionSource<RecordingSession> FinalizeTcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? MonitorTask { get; set; }
    }
}
