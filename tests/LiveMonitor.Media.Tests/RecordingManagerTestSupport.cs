using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Media.Tests;

/// <summary>手工推进的时钟，避免用真实时间等待卡死判定（30 秒）。</summary>
internal sealed class TestClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset? start = null)
        => _now = start ?? new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public DateTimeOffset Read() => Now;

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }
}

internal sealed class FakeRoomRepository : IRoomRepository
{
    public List<LiveRoom> Rooms { get; } = new();

    public Task<IReadOnlyList<LiveRoom>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LiveRoom>>(Rooms.ToList());

    public Task<LiveRoom?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Rooms.FirstOrDefault(room => room.Id == id));

    public Task<bool> ExistsAsync(PlatformType platform, string roomId, CancellationToken ct = default)
        => Task.FromResult(Rooms.Any(room =>
            room.Platform == platform
            && string.Equals(room.RoomId, roomId, StringComparison.OrdinalIgnoreCase)));

    public Task<int> GetNextDisplayNumberAsync(CancellationToken ct = default)
        => Task.FromResult(Rooms.Count == 0 ? 1 : Rooms.Max(room => room.DisplayNumber) + 1);

    public Task AddAsync(LiveRoom room, CancellationToken ct = default)
    {
        Rooms.Add(room);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(LiveRoom room, CancellationToken ct = default)
    {
        var index = Rooms.FindIndex(existing => existing.Id == room.Id);
        if (index >= 0)
        {
            Rooms[index] = room;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        Rooms.RemoveAll(room => room.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRecordingTargetRepository : IRecordingTargetRepository
{
    public Dictionary<Guid, RecordingTarget> Targets { get; } = new();

    public Task<IReadOnlyList<RecordingTarget>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingTarget>>(
            Targets.Values.Select(Clone).OrderBy(target => target.UpdatedAt).ToList());

    public Task<RecordingTarget?> GetAsync(Guid liveRoomId, CancellationToken ct = default)
        => Task.FromResult(Targets.TryGetValue(liveRoomId, out var target) ? Clone(target) : null);

    public Task UpsertAsync(RecordingTarget target, CancellationToken ct = default)
    {
        Targets[target.LiveRoomId] = Clone(target);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid liveRoomId, CancellationToken ct = default)
    {
        Targets.Remove(liveRoomId);
        return Task.CompletedTask;
    }

    public void Set(Guid liveRoomId, bool autoRecordEnabled, string? customSaveDirectory = null)
        => Targets[liveRoomId] = new RecordingTarget
        {
            LiveRoomId = liveRoomId,
            AutoRecordEnabled = autoRecordEnabled,
            CustomSaveDirectory = customSaveDirectory,
            UpdatedAt = DateTime.UtcNow,
        };

    private static RecordingTarget Clone(RecordingTarget target) => new()
    {
        LiveRoomId = target.LiveRoomId,
        AutoRecordEnabled = target.AutoRecordEnabled,
        CustomSaveDirectory = target.CustomSaveDirectory,
        UpdatedAt = target.UpdatedAt,
    };
}

internal sealed class FakeRecordingSessionRepository : IRecordingSessionRepository
{
    private readonly Dictionary<Guid, RecordingSession> _sessions = new();

    public Task AddAsync(RecordingSession session, CancellationToken ct = default)
    {
        _sessions[session.Id] = Clone(session);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(RecordingSession session, CancellationToken ct = default)
    {
        if (!_sessions.ContainsKey(session.Id))
        {
            throw new KeyNotFoundException($"录制会话不存在：{session.Id}。");
        }

        _sessions[session.Id] = Clone(session);
        return Task.CompletedTask;
    }

    public Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_sessions.TryGetValue(id, out var session) ? Clone(session) : null);

    public Task<IReadOnlyList<RecordingSession>> GetRecentAsync(int limit = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(
            _sessions.Values.OrderByDescending(session => session.StartedAt).Take(limit).Select(Clone).ToList());

    public Task<IReadOnlyList<RecordingSession>> GetByRoomAsync(
        PlatformType platform,
        string roomId,
        int limit = 100,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(
            _sessions.Values
                .Where(session => session.Platform == platform
                    && string.Equals(session.RoomId, roomId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(session => session.StartedAt)
                .Take(limit)
                .Select(Clone)
                .ToList());

    public Task<IReadOnlyList<RecordingSession>> GetUnfinishedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(
            _sessions.Values
                .Where(session => session.Status is RecordingStatus.Starting
                    or RecordingStatus.Recording
                    or RecordingStatus.Stopping)
                .Select(Clone)
                .ToList());

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _sessions.Remove(id);
        return Task.CompletedTask;
    }

    public void Seed(RecordingSession session) => _sessions[session.Id] = Clone(session);

    public IReadOnlyList<RecordingSession> Snapshot
        => _sessions.Values.Select(Clone).ToList();

    private static RecordingSession Clone(RecordingSession session) => new()
    {
        Id = session.Id,
        LiveRoomId = session.LiveRoomId,
        Platform = session.Platform,
        RoomId = session.RoomId,
        RoomDisplayName = session.RoomDisplayName,
        StreamTitle = session.StreamTitle,
        Status = session.Status,
        VideoFilePath = session.VideoFilePath,
        DanmakuFilePath = session.DanmakuFilePath,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        DurationSeconds = session.DurationSeconds,
        FileSizeBytes = session.FileSizeBytes,
        DanmakuCount = session.DanmakuCount,
        HighlightCount = session.HighlightCount,
        ErrorMessage = session.ErrorMessage,
    };
}

internal sealed class FakeAppSettingsRepository : IAppSettingsRepository
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Values));

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<PlatformType, string> _cookies = new();

    public Task<string?> GetCookieAsync(PlatformType platform, CancellationToken ct = default)
        => Task.FromResult(_cookies.TryGetValue(platform, out var cookie) ? cookie : null);

    public Task SaveAsync(PlatformType platform, string? accountName, string cookie, CancellationToken ct = default)
    {
        _cookies[platform] = cookie;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(PlatformType platform, CancellationToken ct = default)
    {
        _cookies.Remove(platform);
        return Task.CompletedTask;
    }

    public Task MarkExpiredAsync(PlatformType platform, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> IsExpiredAsync(PlatformType platform, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class FakePlatformProvider : IPlatformProvider
{
    public PlatformType Platform { get; init; } = PlatformType.Bilibili;

    public RoomStatus Status { get; set; } = RoomStatus.Offline("1001", PlatformType.Bilibili, "测试直播间", "测试主播");

    public Exception? StatusException { get; set; }

    public int StatusCalls { get; private set; }

    public string? LastCookie { get; private set; }

    public bool CanHandle(string roomUrl) => true;

    public string ExtractRoomId(string roomUrl)
        => roomUrl[(roomUrl.LastIndexOf('/') + 1)..];

    public Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        StatusCalls++;
        LastCookie = cookie;
        if (StatusException is not null)
        {
            throw StatusException;
        }

        return Task.FromResult(Status);
    }
}

internal sealed class FakeFullStreamRecorder : IFullStreamRecorder
{
    public event Action<DateTimeOffset>? OnFirstFrameAnchored;

    public event Action<TimeSpan, long, double>? OnProgressUpdated;

    public event Action<int, string?>? OnExited;

    public StreamQuality? SourceQuality { get; private set; }

    public string? OutputPath { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>模拟从 ffmpeg stderr 解析到的输入时间轴起点（秒）。</summary>
    public double? SourceTimelineStartSeconds { get; set; }

    /// <summary>启动时是否落一个空文件，用于验证删除记录会连带删除录制文件。</summary>
    public bool CreateOutputFile { get; set; }

    public Exception? StartException { get; set; }

    public TaskCompletionSource<bool> StartTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(StreamQuality sourceQuality, string outputMkvPath, CancellationToken ct = default)
    {
        StartCount++;
        SourceQuality = sourceQuality;
        OutputPath = outputMkvPath;
        if (StartException is not null)
        {
            throw StartException;
        }

        if (CreateOutputFile)
        {
            var directory = Path.GetDirectoryName(outputMkvPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(outputMkvPath, Array.Empty<byte>());
        }

        IsRunning = true;
        StartTcs.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task StopGracefullyAsync(TimeSpan timeout)
    {
        StopCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }

    public void RaiseAnchor(DateTimeOffset anchorUtc) => OnFirstFrameAnchored?.Invoke(anchorUtc);

    public void RaiseProgress(TimeSpan duration, long fileSizeBytes, double bitrateKbps)
        => OnProgressUpdated?.Invoke(duration, fileSizeBytes, bitrateKbps);

    public void RaiseExit(int exitCode, string? stderrTail = null)
    {
        IsRunning = false;
        OnExited?.Invoke(exitCode, stderrTail);
    }
}

/// <summary>可控的分片合并器：默认把分片总大小当作合并结果大小，并写出一个输出文件。</summary>
internal sealed class FakeSegmentMerger : ISegmentMerger
{
    public List<string> OutputPaths { get; } = new();

    public List<IReadOnlyList<RecordedSegment>> Batches { get; } = new();

    public SegmentMergeResult Result { get; set; } = new(true, 0, null, TimeSpan.Zero);

    public Exception? Exception { get; set; }

    public Task<SegmentMergeResult> MergeAsync(
        string outputPath,
        IReadOnlyList<RecordedSegment> segments,
        CancellationToken ct = default)
    {
        OutputPaths.Add(outputPath);
        Batches.Add(segments.ToArray());
        if (Exception is not null)
        {
            throw Exception;
        }

        var totalSize = segments.Sum(segment => File.Exists(segment.Path) ? new FileInfo(segment.Path).Length : 0);
        var size = Result.FileSizeBytes > 0 ? Result.FileSizeBytes : totalSize;
        var duration = Result.Duration > TimeSpan.Zero
            ? Result.Duration
            : SegmentOverlapPlanner.ComputeContentDuration(segments);
        if (Result.Merged)
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(outputPath, Array.Empty<byte>());
        }

        return Task.FromResult(Result with { FileSizeBytes = size, Duration = duration });
    }
}

/// <summary>
/// 组装一个真实的 RecordingManager，但把仓储、平台、录制器与时间全部替换成可控 fake：
/// 监控间隔 20ms 便于快速收尾；轮询间隔默认 60 分钟，避免后台轮询干扰手动测试。
/// </summary>
internal sealed class RecordingManagerHarness : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<FakeFullStreamRecorder> _recorders = new();
    private readonly List<RecordingSession> _started = new();
    private readonly List<RecordingSession> _completed = new();
    private readonly List<string> _statusMessages = new();
    private readonly List<string> _logs = new();
    private readonly List<(Guid RoomId, RecordingProgress Progress)> _progressItems = new();

    public RecordingManagerHarness(
        TimeSpan? pollIntervalOverride = null,
        bool readPollIntervalFromSettings = false,
        bool useGlobalDirectoryOverride = true,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            "LiveMonitor.Media.Tests",
            Guid.NewGuid().ToString("N"));
        OutputDirectory = Path.Combine(RootDirectory, "output");
        Directory.CreateDirectory(OutputDirectory);

        Manager = new RecordingManager(
            Rooms,
            Targets,
            Sessions,
            Settings,
            Credentials,
            platform => platform == Provider.Platform ? Provider : null,
            danmaku: null,
            recorderFactory: CreateRecorder,
            globalDirectoryOverride: useGlobalDirectoryOverride ? OutputDirectory : null,
            clock: Clock.Read,
            log: message =>
            {
                lock (_sync)
                {
                    _logs.Add(message);
                }
            },
            monitorInterval: TimeSpan.FromMilliseconds(20),
            pollIntervalOverride: readPollIntervalFromSettings
                ? null
                : pollIntervalOverride ?? TimeSpan.FromMinutes(60),
            delay: delay,
            segmentMerger: SegmentMerger);

        Manager.ProgressUpdated += (roomId, progress) =>
        {
            lock (_sync)
            {
                _progressItems.Add((roomId, progress));
            }
        };
        Manager.SessionStarted += session =>
        {
            lock (_sync)
            {
                _started.Add(session);
            }
        };
        Manager.SessionCompleted += session =>
        {
            lock (_sync)
            {
                _completed.Add(session);
            }
        };
        Manager.StatusMessage += message =>
        {
            lock (_sync)
            {
                _statusMessages.Add(message);
            }
        };
    }

    public string RootDirectory { get; }

    public string OutputDirectory { get; }

    public TestClock Clock { get; } = new();

    public FakeRoomRepository Rooms { get; } = new();

    public FakeRecordingTargetRepository Targets { get; } = new();

    public FakeRecordingSessionRepository Sessions { get; } = new();

    public FakeAppSettingsRepository Settings { get; } = new();

    public FakeCredentialStore Credentials { get; } = new();

    public FakePlatformProvider Provider { get; } = new();

    public FakeSegmentMerger SegmentMerger { get; } = new();

    public RecordingManager Manager { get; }

    public Exception? RecorderFactoryException { get; set; }

    public Exception? NextRecorderStartException { get; set; }

    public List<FakeFullStreamRecorder> Recorders
    {
        get
        {
            lock (_sync)
            {
                return _recorders.ToList();
            }
        }
    }

    public FakeFullStreamRecorder Recorder
    {
        get
        {
            lock (_sync)
            {
                return _recorders.Count > 0
                    ? _recorders[^1]
                    : throw new InvalidOperationException("尚未创建任何录制器。");
            }
        }
    }

    public List<RecordingSession> Started
    {
        get
        {
            lock (_sync)
            {
                return _started.ToList();
            }
        }
    }

    public List<RecordingSession> Completed
    {
        get
        {
            lock (_sync)
            {
                return _completed.ToList();
            }
        }
    }

    public List<RecordingProgress> ProgressItems
    {
        get
        {
            lock (_sync)
            {
                return _progressItems.Select(item => item.Progress).ToList();
            }
        }
    }

    public List<string> StatusMessages
    {
        get
        {
            lock (_sync)
            {
                return _statusMessages.ToList();
            }
        }
    }

    public List<string> Logs
    {
        get
        {
            lock (_sync)
            {
                return _logs.ToList();
            }
        }
    }

    public LiveRoom AddRoom(
        string roomId = "1001",
        string alias = "测试主播",
        PlatformType platform = PlatformType.Bilibili,
        int displayNumber = 1)
    {
        var room = new LiveRoom
        {
            Id = Guid.NewGuid(),
            DisplayNumber = displayNumber,
            Platform = platform,
            RoomId = roomId,
            RawUrl = $"https://live.example.com/{roomId}",
            Alias = alias,
            CreatedAt = Clock.Now.UtcDateTime,
        };
        Rooms.Rooms.Add(room);
        return room;
    }

    public void SetLive(params StreamQuality[] qualities)
    {
        var list = qualities.Length > 0
            ? qualities
            : new[] { Quality("1080p60", 1080, 60) };
        Provider.Status = new RoomStatus(
            "1001",
            Provider.Platform,
            "测试直播间标题",
            "测试主播",
            true,
            list);
    }

    public void SetOffline()
        => Provider.Status = RoomStatus.Offline("1001", Provider.Platform, "测试直播间标题", "测试主播");

    public Task InitializeAsync() => Manager.InitializeAsync();

    public static StreamQuality Quality(
        string id,
        int height,
        int fps,
        string streamUrl = "https://example.com/live.flv")
        => new(id, id, height, fps, streamUrl, new Dictionary<string, string>());

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 5000,
        string? message = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(message ?? "等待条件超时。");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Manager.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不应影响断言结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private IFullStreamRecorder CreateRecorder()
    {
        if (RecorderFactoryException is not null)
        {
            throw RecorderFactoryException;
        }

        var recorder = new FakeFullStreamRecorder
        {
            StartException = NextRecorderStartException,
        };
        NextRecorderStartException = null;
        lock (_sync)
        {
            _recorders.Add(recorder);
        }

        return recorder;
    }
}
