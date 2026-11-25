using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 单个录制会话与录制中心之间的窄接口：会话 worker 只通过它做收尾后的落库、活动表摘除、
/// 事件通知与会话结束后的断流续录调度。
/// <para>
/// 这样 <see cref="RecordingSessionWorker"/> 不需要反向依赖 <see cref="RecordingManager"/> 实例，
/// 会话状态机与录制中心的全局簿记（活动表、抑制、续录循环）保持单向依赖。
/// </para>
/// </summary>
internal interface IRecordingSessionWorkerHost
{
    /// <summary>把会话字段落库（进度心跳、启动失败、收尾结果）；失败只记日志，不应向 worker 抛异常。</summary>
    Task PersistSessionAsync(RecordingSession session, string operation);

    /// <summary>把会话从录制中心的活动表摘除（收尾与启动失败清理共用）。</summary>
    void RemoveActive(RecordingSessionWorker worker);

    /// <summary>
    /// 收尾落盘之后调用：摘除活动表、触发 <c>SessionCompleted</c> 与进度事件、输出状态提示。
    /// 调用返回后 worker 才会完成收尾任务（并发收尾者在此等待）。
    /// </summary>
    void OnSessionFinalized(RecordingSessionWorker worker, RecordingProgress progress, bool userStop);

    /// <summary>收尾异常：摘除活动表并记录日志。</summary>
    void OnSessionFinalizeFailed(RecordingSessionWorker worker, Exception exception);

    /// <summary>实时进度更新（转发给进度事件）。</summary>
    void OnProgress(RecordingSessionWorker worker, RecordingProgress progress);

    /// <summary>输出日志。</summary>
    void Log(string message);

    /// <summary>输出运行期状态提示（界面状态栏）。</summary>
    void NotifyStatus(string message);

    /// <summary>收尾时确认主播是否已下播；查询失败按仍在直播处理。</summary>
    Task<bool> IsRoomOfflineAsync(LiveRoom room);

    /// <summary>自然中断收尾后按录制偏好安排断流续录。</summary>
    Task MaybeScheduleRetryAsync(LiveRoom room);
}

/// <summary>
/// 录制会话 worker 的共享依赖：录制器工厂、分片合并、时间与延时、高能绑定、弹幕管理与宿主回调。
/// 由 <see cref="RecordingManager"/> 构造并注入，同一管理器下的所有会话复用一份。
/// </summary>
internal sealed class RecordingSessionWorkerContext
{
    public RecordingSessionWorkerContext(
        IRecordingSessionWorkerHost host,
        RecordingHighlightTracker highlightTracker,
        DanmakuManager? danmaku,
        Func<IFullStreamRecorder> recorderFactory,
        ISegmentMerger? segmentMerger,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan monitorInterval)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        HighlightTracker = highlightTracker ?? throw new ArgumentNullException(nameof(highlightTracker));
        Danmaku = danmaku;
        RecorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        SegmentMerger = segmentMerger;
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Delay = delay ?? throw new ArgumentNullException(nameof(delay));
        MonitorInterval = monitorInterval;
    }

    /// <summary>录制中心宿主回调。</summary>
    public IRecordingSessionWorkerHost Host { get; }

    /// <summary>高能事件绑定跟踪器（挂接到 DanmakuManager 的 enricher）。</summary>
    public RecordingHighlightTracker HighlightTracker { get; }

    /// <summary>共享弹幕管理器；为 null 时不写弹幕伴随文件。</summary>
    public DanmakuManager? Danmaku { get; }

    /// <summary>分段录制器工厂（每段一个实例）。</summary>
    public Func<IFullStreamRecorder> RecorderFactory { get; }

    /// <summary>可注入的分片合并器；为 null 时按需创建 ffmpeg 合并器。</summary>
    public ISegmentMerger? SegmentMerger { get; }

    /// <summary>时钟（测试可注入）。</summary>
    public Func<DateTimeOffset> Clock { get; }

    /// <summary>延时（测试可注入）。</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; }

    /// <summary>监控循环节拍。</summary>
    public TimeSpan MonitorInterval { get; }
}

/// <summary>
/// 一路录制会话的状态机：持有分段录制器、分片清单与弹幕伴随文件，负责首帧锚定、进度心跳、
/// 卡死判定、断流续段，并在收尾时合并分片、确定会话终态（Completed/Failed）。
/// <para>
/// 线程安全：所有可被录制器回调、监控循环、停止请求与「边录边剪」快照并发访问的会话字段
/// 都由 <c>_sync</c> 保护；<see cref="BuildLiveRecordingInfo"/> 在锁内一致读取分片路径与累计时长，
/// 供 UI 线程在录制过程中随时取快照。收尾用 <see cref="FinalizeAsync"/> 的一次性闸门保证
/// 「并发停止 / 自然结束」只收尾一次，其余调用共享同一个收尾任务。
/// </para>
/// <para>
/// 生命周期：创建 → <see cref="StartAsync"/>（失败时 <see cref="CleanupStartFailureAsync"/>）→
/// <see cref="StartMonitoring"/> → 收尾 <see cref="FinalizeAsync"/>。全局簿记（活动表、抑制、
/// 断流续录）由宿主 <see cref="IRecordingSessionWorkerHost"/> 负责。
/// </para>
/// </summary>
internal sealed class RecordingSessionWorker
{
    /// <summary>连续多少段都失败（启动即退出）后放弃自动续段，交给断流续录/开播轮询。</summary>
    internal const int MaxConsecutivePartRestarts = 6;

    /// <summary>手动停止时等待 ffmpeg 优雅收尾的最长时间。</summary>
    internal static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>监控循环发现进度超过该时长无更新时判定流卡死。</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>录制中的进度心跳落库间隔。</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    /// <summary>分段续录：ffmpeg 因 CDN 断流退出后，重启下一段前的等待时间。</summary>
    private static readonly TimeSpan PartRestartDelay = TimeSpan.FromSeconds(2);

    private readonly IFullStreamRecorder _initialRecorder;
    private readonly IPlatformProvider _provider;
    private readonly string? _cookie;
    private readonly StreamQuality _quality;
    private readonly DateTimeOffset _recordingStartUtc;
    private readonly RecordingSessionWorkerContext _context;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<RecordingSession> _finalizeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RecordedSegment> _parts = new();

    private IFullStreamRecorder? _recorder;
    private DanmakuJsonlRecorder? _jsonl;
    private DanmakuSessionHandle? _handle;
    private DateTimeOffset _currentPartStartedAt;
    private TimeSpan _completedDuration;
    private long _completedSize;
    private TimeSpan _currentPartDuration;
    private long _currentPartSize;
    private string? _currentPartPath;
    private int _partIndex;
    private int _partFailures;
    private string? _partFailureMessage;
    private int _stopRequested;
    private bool _stallDetected;
    private bool _recorderExited;
    private int _exitCode;
    private string? _stderrTail;
    private DateTimeOffset? _anchorUtc;
    private DateTimeOffset _lastProgressUtc;
    private double _lastBitrate;
    private int _danmakuCount;
    private int _finalizeStarted;

    public RecordingSessionWorker(
        LiveRoom room,
        RecordingSession session,
        IFullStreamRecorder recorder,
        IPlatformProvider provider,
        string? cookie,
        StreamQuality quality,
        DateTimeOffset recordingStartUtc,
        RecordingSessionWorkerContext context)
    {
        Room = room ?? throw new ArgumentNullException(nameof(room));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _initialRecorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cookie = cookie;
        _quality = quality ?? throw new ArgumentNullException(nameof(quality));
        _recordingStartUtc = recordingStartUtc;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _recorder = recorder;

        _currentPartStartedAt = recordingStartUtc;
        _lastProgressUtc = recordingStartUtc;
    }

    /// <summary>直播间主数据快照。</summary>
    public LiveRoom Room { get; }

    /// <summary>本次录制的会话实体（收尾时由 worker 更新终态）。</summary>
    public RecordingSession Session { get; }

    /// <summary>
    /// 是否已被请求停止（手动停止/管理器关闭）。可被并发置位，
    /// 用 Volatile 读写保证监控循环与收尾路径能及时看到。
    /// </summary>
    public bool StopRequested
    {
        get => Volatile.Read(ref _stopRequested) != 0;
        set => Interlocked.Exchange(ref _stopRequested, value ? 1 : 0);
    }

    /// <summary>当前分段录制器；分段切换与收尾期间可能为 null。</summary>
    public IFullStreamRecorder? Recorder
    {
        get
        {
            lock (_sync)
            {
                return _recorder;
            }
        }
    }

    /// <summary>监控循环任务（成功起录后启动）；启动失败时为 null。</summary>
    public Task? MonitorTask { get; private set; }

    /// <summary>
    /// 启动首段录制：创建弹幕伴随文件、订阅弹幕与录制器事件、登记高能绑定、
    /// 写入首分片路径并拉起 ffmpeg。失败时调用方应调用 <see cref="CleanupStartFailureAsync"/>。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var danmakuPath = Session.DanmakuFilePath;
        if (!string.IsNullOrWhiteSpace(danmakuPath))
        {
            try
            {
                _jsonl = new DanmakuJsonlRecorder(
                    danmakuPath,
                    Session.Id,
                    Room.Platform,
                    Session.RoomId,
                    _context.Host.Log);
            }
            catch (Exception ex)
            {
                _context.Host.Log($"弹幕伴随文件创建失败，本次录制不写 .jsonl（{Session.RoomDisplayName}）：{ex.Message}");
            }
        }

        AttachDanmaku();
        _context.HighlightTracker.Register(Room.Platform, Session.RoomId, Session.Id);
        SubscribeRecorder(_initialRecorder);

        var firstPartPath = BuildPartPath(Session.VideoFilePath, 1);
        lock (_sync)
        {
            _currentPartPath = firstPartPath;
            _partIndex = 1;
            _currentPartStartedAt = _context.Clock();
        }

        await _initialRecorder.StartAsync(_quality, firstPartPath, ct).ConfigureAwait(false);
    }

    /// <summary>启动监控循环：进度心跳落库、卡死判定、断流自动续段与自然结束收尾。</summary>
    public void StartMonitoring()
    {
        MonitorTask = Task.Run(() => MonitorAsync(), CancellationToken.None);
    }

    /// <summary>
    /// 启动失败清理：解绑弹幕/高能、释放已建资源、删除半成品文件，并把会话标记为 Failed 落库。
    /// </summary>
    public async Task CleanupStartFailureAsync(Exception startError)
    {
        var session = Session;
        DetachDanmaku();
        _context.HighlightTracker.Remove(Room.Platform, session.RoomId);
        _context.Host.RemoveActive(this);

        var jsonl = _jsonl;
        _jsonl = null;
        if (jsonl is not null)
        {
            try
            {
                await jsonl.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context.Host.Log($"启动失败清理弹幕文件异常：{ex.Message}");
            }
        }

        try
        {
            var recorder = _recorder;
            _recorder = null;
            if (recorder is not null)
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _context.Host.Log($"启动失败清理录制器异常：{ex.Message}");
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();

        // 启动失败的分段没有保留价值：清掉可能的半成品文件，并把会话标记为 Failed 供历史列表展示。
        List<string?> failedPaths;
        lock (_sync)
        {
            failedPaths = _parts.Select(part => (string?)part.Path).ToList();
            failedPaths.Add(_currentPartPath);
        }

        foreach (var path in failedPaths)
        {
            TryDeleteQuietly(path);
        }

        TryDeleteQuietly(session.VideoFilePath);
        TryDeleteQuietly(session.DanmakuFilePath);
        session.Status = RecordingStatus.Failed;
        session.EndedAt = _context.Clock().UtcDateTime;
        session.ErrorMessage = $"启动录制失败：{startError.Message}";
        await _context.Host.PersistSessionAsync(session, "标记录制启动失败").ConfigureAwait(false);
    }

    /// <summary>
    /// 收尾（幂等）：停掉弹幕句柄/录制器、合并分片、确定会话终态并落库，然后回调宿主
    /// 写事件、摘除活动表并安排断流续录。并发调用共享同一个收尾任务。
    /// </summary>
    public async Task<RecordingSession> FinalizeAsync(bool requestedStop)
    {
        if (Interlocked.Exchange(ref _finalizeStarted, 1) != 0)
        {
            return await _finalizeTcs.Task.ConfigureAwait(false);
        }

        var session = Session;
        var userStop = requestedStop || StopRequested;
        try
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            DetachDanmaku();
            var jsonl = _jsonl;
            _jsonl = null;
            if (jsonl is not null)
            {
                try
                {
                    await jsonl.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Host.Log($"收尾弹幕文件异常（{session.RoomDisplayName}）：{ex.Message}");
                }
            }

            var recorder = _recorder;
            _recorder = null;
            if (recorder is not null)
            {
                try
                {
                    await recorder.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Host.Log($"收尾录制器异常（{session.RoomDisplayName}）：{ex.Message}");
                }
            }

            CaptureCurrentPart(recorder);

            // 分段录制：停录后把所有分片合并成一个文件；合并失败时分片保留并在会话里注明。
            var mergeResult = await MergePartsAsync(CancellationToken.None).ConfigureAwait(false);
            int partCount;
            lock (_sync)
            {
                partCount = _parts.Count;
            }

            var mergeNote = mergeResult.Merged
                ? null
                : $"录制分片合并失败：{mergeResult.ErrorMessage}（{partCount} 个分片已保留在录制目录）";

            var endedAt = _context.Clock();
            var highlightCount = _context.HighlightTracker.Remove(Room.Platform, session.RoomId);
            var fileSize = mergeResult.FileSizeBytes;
            var written = jsonl?.WrittenCount ?? 0;

            // 开播状态查询是异步 IO，必须在 lock 外完成。
            bool offline = false;
            bool stallDetected;
            lock (_sync)
            {
                stallDetected = _stallDetected;
            }

            if (!userStop && !stallDetected)
            {
                try
                {
                    offline = await _context.Host.IsRoomOfflineAsync(Room).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Host.Log($"收尾时确认开播状态失败（{session.RoomDisplayName}）：{ex.Message}");
                }
            }

            RecordingProgress progress;
            lock (_sync)
            {
                var durationAnchor = _anchorUtc ?? _recordingStartUtc;
                var recordedDuration = _completedDuration + _currentPartDuration;
                var mergedContentDuration = mergeResult.Merged && mergeResult.Duration > TimeSpan.Zero
                    ? mergeResult.Duration
                    : (TimeSpan?)null;

                session.EndedAt = endedAt.UtcDateTime;
                if (mergedContentDuration is { } mergedDuration)
                {
                    // 合并时裁掉了 CDN 重发的重叠内容：以合并结果为准，纠正录制期间按分片累加导致的虚高时长。
                    session.DurationSeconds = mergedDuration.TotalSeconds;
                }
                else
                {
                    session.DurationSeconds = Math.Max(session.DurationSeconds, recordedDuration.TotalSeconds);
                    session.DurationSeconds = Math.Max(
                        session.DurationSeconds,
                        Math.Max(0, (endedAt - durationAnchor).TotalSeconds));
                }

                if (mergeResult.Merged && mergeResult.FileSizeBytes > 0)
                {
                    // 合并时裁掉了重叠内容：以合并结果的字节数为准，避免录制期间按分片累加的虚高大小。
                    session.FileSizeBytes = mergeResult.FileSizeBytes;
                }
                else
                {
                    session.FileSizeBytes = Math.Max(
                        session.FileSizeBytes,
                        Math.Max(_completedSize + _currentPartSize, fileSize));
                }

                session.DanmakuCount = Math.Max(session.DanmakuCount, Math.Max(written, Volatile.Read(ref _danmakuCount)));
                session.HighlightCount = Math.Max(session.HighlightCount, highlightCount);

                if (userStop)
                {
                    session.Status = RecordingStatus.Completed;
                    session.ErrorMessage = mergeNote;
                }
                else if (_stallDetected)
                {
                    session.Status = RecordingStatus.Failed;
                    session.ErrorMessage = $"录制流卡死超过 {StallTimeout.TotalSeconds:0} 秒，已强制结束当前分段。";
                    if (mergeNote is not null)
                    {
                        session.ErrorMessage += " " + mergeNote;
                    }
                }
                else
                {
                    if (offline)
                    {
                        session.Status = RecordingStatus.Completed;
                        session.ErrorMessage = mergeNote;
                    }
                    else
                    {
                        session.Status = RecordingStatus.Failed;
                        session.ErrorMessage = _partFailureMessage is { Length: > 0 } partError
                            ? $"直播流中断后连续 {MaxConsecutivePartRestarts} 段录制启动失败：{partError}"
                            : DescribeExit();
                        if (mergeNote is not null)
                        {
                            session.ErrorMessage += " " + mergeNote;
                        }
                    }
                }

                progress = new RecordingProgress(
                    session.Id,
                    TimeSpan.FromSeconds(session.DurationSeconds),
                    session.FileSizeBytes,
                    _lastBitrate,
                    session.DanmakuCount,
                    session.HighlightCount);
            }

            await _context.Host.PersistSessionAsync(session, "录制收尾落盘").ConfigureAwait(false);

            _context.Host.OnSessionFinalized(this, progress, userStop);

            _finalizeTcs.TrySetResult(session);

            if (!userStop)
            {
                await _context.Host.MaybeScheduleRetryAsync(Room).ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception ex)
        {
            _finalizeTcs.TrySetException(ex);
            _context.Host.OnSessionFinalizeFailed(this, ex);
            throw;
        }
    }

    /// <summary>把会话当前进度写进会话实体并落库（监控循环心跳）。</summary>
    private async Task PersistProgressAsync()
    {
        var session = Session;
        lock (_sync)
        {
            var recorded = _completedDuration + _currentPartDuration;
            session.DurationSeconds = Math.Max(session.DurationSeconds, recorded.TotalSeconds);
            session.FileSizeBytes = Math.Max(session.FileSizeBytes, _completedSize + _currentPartSize);
            session.DanmakuCount = Math.Max(session.DanmakuCount, Volatile.Read(ref _danmakuCount));
            session.HighlightCount = Math.Max(
                session.HighlightCount,
                _context.HighlightTracker.GetHighlightCount(session.Id));
        }

        await _context.Host.PersistSessionAsync(session, "录制进度落盘").ConfigureAwait(false);
    }

    private void SubscribeRecorder(IFullStreamRecorder recorder)
    {
        recorder.OnFirstFrameAnchored += OnAnchor;
        recorder.OnProgressUpdated += OnProgress;
        recorder.OnExited += OnRecorderExited;
    }

    private void AttachDanmaku()
    {
        var danmaku = _context.Danmaku;
        if (danmaku is null)
        {
            return;
        }

        try
        {
            var handle = danmaku.Acquire(Room.Platform, Room.RoomId);
            _handle = handle;
            handle.MessagesReceived += OnDanmakuBatch;
        }
        catch (Exception ex)
        {
            _context.Host.Log($"弹幕订阅失败，本次录制不写弹幕伴随文件（{Session.RoomDisplayName}）：{ex.Message}");
        }
    }

    private void DetachDanmaku()
    {
        var handle = _handle;
        _handle = null;
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
            _context.Host.Log($"释放弹幕句柄异常：{ex.Message}");
        }
    }

    private void OnDanmakuBatch(IReadOnlyList<DanmakuMessage> batch)
    {
        var jsonl = _jsonl;
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
            Interlocked.Add(ref _danmakuCount, written);
        }
    }

    private void OnAnchor(DateTimeOffset anchorUtc)
    {
        lock (_sync)
        {
            if (_anchorUtc is null)
            {
                _anchorUtc = anchorUtc;
                Session.StartedAt = anchorUtc.UtcDateTime;
                _lastProgressUtc = anchorUtc;
            }
        }

        _context.HighlightTracker.UpdateAnchor(Session.Id, anchorUtc);
        try
        {
            _jsonl?.UpdateAnchor(anchorUtc);
        }
        catch (Exception ex)
        {
            _context.Host.Log($"校准弹幕文件锚点异常：{ex.Message}");
        }
    }

    private void OnProgress(TimeSpan duration, long fileSizeBytes, double bitrateKbps)
    {
        lock (_sync)
        {
            _currentPartDuration = duration;
            _currentPartSize = fileSizeBytes;
            _lastBitrate = bitrateKbps;
            _lastProgressUtc = _context.Clock();
            _partFailures = 0;
            var totalDuration = _completedDuration + duration;
            var totalSize = _completedSize + fileSizeBytes;
            Session.DurationSeconds = Math.Max(Session.DurationSeconds, totalDuration.TotalSeconds);
            Session.FileSizeBytes = Math.Max(Session.FileSizeBytes, totalSize);
        }

        _context.Host.OnProgress(this, BuildProgress());
    }

    private void OnRecorderExited(int exitCode, string? stderrTail)
    {
        lock (_sync)
        {
            _recorderExited = true;
            _exitCode = exitCode;
            _stderrTail = stderrTail;
        }
    }

    private async Task MonitorAsync()
    {
        CancellationToken token;
        try
        {
            token = _cts.Token;
        }
        catch (ObjectDisposedException)
        {
            // 启动失败清理路径可能已释放 Cts，此时无需再监控。
            return;
        }

        var nextPersist = _context.Clock() + PersistInterval;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _context.Delay(_context.MonitorInterval, token).ConfigureAwait(false);

                bool stopRequested;
                bool recorderExited;
                DateTimeOffset lastProgress;
                lock (_sync)
                {
                    stopRequested = StopRequested;
                    recorderExited = _recorderExited;
                    lastProgress = _lastProgressUtc;
                }

                if (stopRequested)
                {
                    break;
                }

                if (recorderExited)
                {
                    // 虎牙等 CDN 会周期性断开连接：当前分片正常结束后自动开下一段，
                    // 全部段落停止录制时再合并，避免单次 ffmpeg 重连写坏 matroska。
                    if (!await TryStartNextPartAsync(token).ConfigureAwait(false))
                    {
                        break;
                    }

                    nextPersist = _context.Clock() + PersistInterval;
                    continue;
                }

                var now = _context.Clock();
                if (lastProgress != default && now - lastProgress > StallTimeout)
                {
                    lock (_sync)
                    {
                        _stallDetected = true;
                    }

                    _context.Host.Log($"录制进度超过 {StallTimeout.TotalSeconds:0} 秒无更新，判定流卡死，终止当前分段：{Session.RoomDisplayName}");
                    var stalledJsonl = _jsonl;
                    _jsonl = null;
                    if (stalledJsonl is not null)
                    {
                        try
                        {
                            await stalledJsonl.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _context.Host.Log($"卡死收尾弹幕文件异常：{ex.Message}");
                        }
                    }

                    IFullStreamRecorder? stalledRecorder;
                    lock (_sync)
                    {
                        stalledRecorder = _recorder;
                    }

                    if (stalledRecorder is not null)
                    {
                        try
                        {
                            await stalledRecorder.StopGracefullyAsync(GracefulStopTimeout).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _context.Host.Log($"卡死终止录制器异常：{ex.Message}");
                        }
                    }

                    break;
                }

                if (now >= nextPersist)
                {
                    nextPersist = now + PersistInterval;
                    await PersistProgressAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 管理器停止/收尾时取消，正常退出。
        }
        catch (Exception ex)
        {
            _context.Host.Log($"录制监控循环异常（{Session.RoomDisplayName}）：{ex.Message}");
        }

        try
        {
            if (!StopRequested && !token.IsCancellationRequested)
            {
                try
                {
                    await FinalizeAsync(requestedStop: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Host.Log($"录制自动收尾异常（{Session.RoomDisplayName}）：{ex.Message}");
                }
            }
        }
        finally
        {
            // Cts 只在监控循环退出时释放，避免与收尾路径的 Cancel 竞态。
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// 当前分片结束（CDN 主动断流）后尝试开下一段：重新解析房间状态拿新的签名地址，
    /// 仍开播则新建录制器与分片文件继续录；主播下播或连续启动失败时返回 false。
    /// </summary>
    private async Task<bool> TryStartNextPartAsync(CancellationToken token)
    {
        IFullStreamRecorder? previous;
        lock (_sync)
        {
            previous = _recorder;
            _recorder = null;
        }

        if (previous is not null)
        {
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context.Host.Log($"重置录制器异常（{Session.RoomDisplayName}）：{ex.Message}");
            }
        }

        CaptureCurrentPart(previous);

        lock (_sync)
        {
            _recorderExited = false;
            _exitCode = 0;
            _stderrTail = null;
        }

        if (StopRequested)
        {
            return false;
        }

        RoomStatus? status = null;
        try
        {
            status = await _provider.GetRoomStatusAsync(Room.RoomId, _cookie, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context.Host.Log($"断流后重新查询开播状态失败（{Session.RoomDisplayName}）：{ex.Message}");
        }

        if (status is null || !status.IsLive)
        {
            return false;
        }

        var quality = ChooseQuality(status) ?? _quality;
        for (var attempt = 0; attempt < MaxConsecutivePartRestarts; attempt++)
        {
            if (StopRequested)
            {
                return false;
            }

            int partNumber;
            lock (_sync)
            {
                partNumber = _partIndex + 1;
            }

            var partPath = BuildPartPath(Session.VideoFilePath, partNumber);
            IFullStreamRecorder? recorder = null;
            try
            {
                recorder = _context.RecorderFactory();
                SubscribeRecorder(recorder);
                await recorder.StartAsync(quality, partPath, token).ConfigureAwait(false);
                lock (_sync)
                {
                    _recorder = recorder;
                    _partIndex = partNumber;
                    _partFailures = 0;
                    _lastProgressUtc = _context.Clock();
                    _currentPartPath = partPath;
                    _currentPartStartedAt = _context.Clock();
                }

                _context.Host.NotifyStatus($"直播流中断，已开始第 {partNumber} 段续录：{Session.RoomDisplayName}");
                _context.Host.Log($"分段续录：第 {partNumber} 段 → {partPath}");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _context.Host.Log($"启动第 {partNumber} 段录制失败（{Session.RoomDisplayName}）：{ex.Message}");
                if (recorder is not null)
                {
                    try
                    {
                        await recorder.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        _context.Host.Log($"清理失败分段的录制器异常：{disposeEx.Message}");
                    }
                }

                bool exhausted;
                lock (_sync)
                {
                    _partFailures++;
                    exhausted = _partFailures >= MaxConsecutivePartRestarts;
                    _partFailureMessage = ex.Message;
                }

                if (exhausted)
                {
                    _context.Host.Log($"连续 {MaxConsecutivePartRestarts} 段启动失败，停止自动续段：{Session.RoomDisplayName}");
                    return false;
                }

                await _context.Delay(PartRestartDelay, token).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>把当前分片登记到已完成列表：记录路径、时长与输入时间轴起点，供合并时裁掉 CDN 重发的重叠内容。</summary>
    private void CaptureCurrentPart(IFullStreamRecorder? recorder)
    {
        lock (_sync)
        {
            if (_currentPartPath is not { } path)
            {
                return;
            }

            var offset = _currentPartStartedAt - _recordingStartUtc;
            if (offset < TimeSpan.Zero)
            {
                offset = TimeSpan.Zero;
            }

            _parts.Add(new RecordedSegment(
                path,
                _currentPartDuration,
                recorder?.SourceTimelineStartSeconds,
                offset));
            _completedDuration += _currentPartDuration;
            _completedSize += _currentPartSize;
            _currentPartDuration = TimeSpan.Zero;
            _currentPartSize = 0;
            _currentPartPath = null;
        }
    }

    /// <summary>把本场录制的所有分片合并成 final 文件；没有分片或合并失败时保留分片。</summary>
    private async Task<SegmentMergeResult> MergePartsAsync(CancellationToken ct)
    {
        List<RecordedSegment> parts;
        lock (_sync)
        {
            parts = _parts.ToList();
        }

        if (parts.Count == 0)
        {
            return new SegmentMergeResult(false, 0, "没有录制分片。", TimeSpan.Zero);
        }

        var merger = _context.SegmentMerger;
        if (merger is null)
        {
            try
            {
                merger = new FfmpegSegmentMerger(FfmpegLocator.Locate(), _context.Host.Log);
            }
            catch (Exception ex)
            {
                return new SegmentMergeResult(
                    false,
                    parts.Sum(part => TryGetFileLength(part.Path)),
                    $"未找到可用于合并分片的 ffmpeg：{ex.Message}",
                    TimeSpan.Zero);
            }
        }

        return await merger.MergeAsync(Session.VideoFilePath, parts, ct).ConfigureAwait(false);
    }

    private string DescribeExit()
        => RecordingDirectoryGuard.DescribeFfmpegFailure(
            _exitCode,
            _stderrTail,
            RecordingDirectoryGuard.TryGetDirectory(Session.VideoFilePath));

    /// <summary>实时进度快照：累计时长/大小/码率/弹幕/高能；未产生进度前按锚点墙钟估算。</summary>
    public RecordingProgress BuildProgress()
    {
        lock (_sync)
        {
            var recorded = _completedDuration + _currentPartDuration;
            var size = _completedSize + _currentPartSize;
            return new RecordingProgress(
                Session.Id,
                recorded > TimeSpan.Zero
                    ? recorded
                    : TimeSpan.FromSeconds(Math.Max(0, (_context.Clock() - (_anchorUtc ?? _recordingStartUtc)).TotalSeconds)),
                Math.Max(size, Session.FileSizeBytes),
                _lastBitrate,
                Math.Max(Session.DanmakuCount, Volatile.Read(ref _danmakuCount)),
                Math.Max(Session.HighlightCount, _context.HighlightTracker.GetHighlightCount(Session.Id)));
        }
    }

    /// <summary>
    /// 构造「边录边剪」用的实时快照：当前正在写的分片路径 + 分片序号 + 累计时长/大小/弹幕/高能。
    /// 全部字段在状态锁内一致读取；分段切换的瞬间当前分片路径可能为空，此时按分片序号兜底拼路径。
    /// </summary>
    public LiveRecordingInfo BuildLiveRecordingInfo()
    {
        var progress = BuildProgress();

        TimeSpan completedDuration;
        int partNumber;
        string partFilePath;
        lock (_sync)
        {
            completedDuration = _completedDuration;
            partNumber = Math.Max(1, _partIndex);
            partFilePath = _currentPartPath
                           ?? BuildPartPath(Session.VideoFilePath, partNumber);
        }

        return new LiveRecordingInfo(
            Session.Id,
            Room.Id,
            Room.DisplayNumber,
            Session.RoomDisplayName,
            partFilePath,
            partNumber,
            completedDuration,
            progress.Duration,
            progress.FileSizeBytes,
            progress.DanmakuCount,
            progress.HighlightCount);
    }

    /// <summary>从房间状态里挑清晰度：优先高分辨率，其次高帧率，跳过没有拉流地址的画质。</summary>
    internal static StreamQuality? ChooseQuality(RoomStatus status)
        => status.AvailableQualities
            .Where(quality => !string.IsNullOrWhiteSpace(quality.StreamUrl))
            .OrderByDescending(quality => quality.Height)
            .ThenByDescending(quality => quality.Fps)
            .FirstOrDefault();

    /// <summary>探测文件字节数；文件不存在或无法访问时返回 0（崩溃恢复与合并失败兜底共用）。</summary>
    internal static long TryGetFileLength(string? path)
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
            _context.Host.Log($"清理启动失败产物异常（{path}）：{ex.Message}");
        }
    }

    private static string BuildPartPath(string videoPath, int partNumber)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(directory, $"{name}.part{partNumber:000}{Path.GetExtension(videoPath)}");
    }
}
