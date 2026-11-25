using System.Collections.Concurrent;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// RecordingSessionWorker 的会话内状态机测试（阶段五 5.1 从 RecordingManager 抽离后的新接缝）：
/// 实时快照线程安全、收尾回调恰好一次、并发停止与自然结束、启动失败清理。
/// </summary>
public sealed class RecordingSessionWorkerTests
{
    [Fact]
    public async Task StartAsync_InitializesFirstPartAndRegistersHighlightBinding()
    {
        await using var harness = new WorkerHarness();
        harness.SetLive();
        var worker = harness.CreateWorker();

        await worker.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var recorder = harness.Recorders[0];
        Assert.Equal(1, recorder.StartCount);
        Assert.EndsWith(".part001.mkv", recorder.OutputPath!);
        Assert.Equal(1, harness.Tracker.Count);

        var info = worker.BuildLiveRecordingInfo();
        Assert.Equal(worker.Session.Id, info.SessionId);
        Assert.Equal(harness.Room.Id, info.LiveRoomId);
        Assert.Equal(harness.Room.DisplayNumber, info.RoomDisplayNumber);
        Assert.Equal("#1 测试主播", info.RoomDisplayName);
        Assert.Equal(1, info.PartNumber);
        Assert.EndsWith(".part001.mkv", info.PartFilePath);
        Assert.Equal(TimeSpan.Zero, info.CompletedDuration);
    }

    [Fact]
    public async Task BuildProgressAndLiveInfo_ReflectRecorderProgress()
    {
        await using var harness = new WorkerHarness();
        harness.SetLive();
        var worker = harness.CreateWorker();
        await worker.StartAsync();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        recorder.RaiseProgress(TimeSpan.FromSeconds(3), 8192, 2500);

        var progress = worker.BuildProgress();
        Assert.Equal(TimeSpan.FromSeconds(3), progress.Duration);
        Assert.Equal(8192L, progress.FileSizeBytes);
        Assert.Equal(2500, progress.BitrateKbps, 3);
        Assert.Single(harness.Host.Progresses);

        var info = worker.BuildLiveRecordingInfo();
        Assert.Equal(TimeSpan.FromSeconds(3), info.RecordedDuration);
        Assert.Equal(TimeSpan.Zero, info.CompletedDuration);
        Assert.Equal(8192L, info.FileSizeBytes);
    }

    [Fact]
    public async Task BuildLiveRecordingInfo_AfterPartSwitch_ReportsNextPartAndCompletedDuration()
    {
        await using var harness = new WorkerHarness();
        harness.SetLive();
        var worker = harness.CreateWorker();
        await worker.StartAsync();
        worker.StartMonitoring();

        var firstPart = harness.Recorders[0];
        firstPart.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        firstPart.RaiseProgress(TimeSpan.FromSeconds(2), 2048, 800);

        firstPart.RaiseExit(1, "stream ends prematurely");

        await RecordingManagerHarness.WaitUntilAsync(
            () => harness.Recorders.Count >= 2 && worker.BuildLiveRecordingInfo().PartNumber == 2,
            timeoutMilliseconds: 8000,
            message: "等待断流后第二段录制启动超时。");

        var info = worker.BuildLiveRecordingInfo();
        Assert.Equal(2, info.PartNumber);
        Assert.EndsWith(".part002.mkv", info.PartFilePath);
        Assert.Equal(TimeSpan.FromSeconds(2), info.CompletedDuration);
        Assert.Equal(TimeSpan.FromSeconds(2), info.RecordedDuration);
        Assert.True(harness.Provider.StatusCalls >= 1);
    }

    [Fact]
    public async Task BuildLiveRecordingInfo_ConcurrentReaders_StayConsistent()
    {
        await using var harness = new WorkerHarness();
        harness.SetLive();
        var worker = harness.CreateWorker();
        await worker.StartAsync();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);

        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var info = worker.BuildLiveRecordingInfo();
                    Assert.Equal(worker.Session.Id, info.SessionId);
                    Assert.False(string.IsNullOrWhiteSpace(info.PartFilePath));
                    Assert.True(info.PartNumber >= 1);
                    Assert.True(info.RecordedDuration >= info.CompletedDuration);
                    Assert.True(info.FileSizeBytes >= 0);
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToArray();

        for (var i = 1; i <= 50; i++)
        {
            recorder.RaiseProgress(TimeSpan.FromMilliseconds(i), 1024L * i, 800);
            await Task.Delay(2);
        }

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
        Assert.Empty(harness.Host.Finalized);
    }

    [Fact]
    public async Task FinalizeAsync_ConcurrentCalls_FinalizesOnceAndPersistsBeforeCallback()
    {
        await using var harness = new WorkerHarness();
        harness.SetLive();
        var worker = harness.CreateWorker();
        await worker.StartAsync();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        recorder.RaiseProgress(TimeSpan.FromSeconds(5), 4096, 1600);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => worker.FinalizeAsync(requestedStop: true)))
            .ToArray();
        var sessions = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(sessions, session => Assert.Same(worker.Session, session));
        Assert.Single(harness.Host.Finalized);
        Assert.Single(harness.Host.PersistOperations, operation => operation == "录制收尾落盘");
        Assert.Single(harness.Merger.Batches);
        Assert.Equal(1, recorder.DisposeCount);
        Assert.Equal(0, harness.Host.FinalizeFailedCount);
        Assert.Equal(RecordingStatus.Completed, worker.Session.Status);

        var events = harness.Host.Events;
        var persistIndex = events.IndexOf("persist:录制收尾落盘");
        var finalizedIndex = events.IndexOf("finalized");
        Assert.True(persistIndex >= 0);
        Assert.True(finalizedIndex > persistIndex);
    }

    [Fact]
    public async Task FinalizeAsync_NaturalEndWhileOffline_CompletesAndSchedulesRetry()
    {
        await using var harness = new WorkerHarness();
        var worker = harness.CreateWorker();
        await worker.StartAsync();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        recorder.RaiseProgress(TimeSpan.FromSeconds(5), 4096, 1600);

        var session = await worker.FinalizeAsync(requestedStop: false);

        Assert.Equal(RecordingStatus.Completed, session.Status);
        Assert.Null(session.ErrorMessage);
        Assert.Single(harness.Host.OfflineProbes);
        Assert.Single(harness.Host.RetryRooms);
        Assert.Single(harness.Host.Finalized);
        Assert.False(harness.Host.FinalizedUserStops[0]);
    }

    [Fact]
    public async Task StartMonitoring_NaturalExitThenManualStop_FinalizesOnce()
    {
        await using var harness = new WorkerHarness();
        var worker = harness.CreateWorker();
        await worker.StartAsync();
        worker.StartMonitoring();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        recorder.RaiseProgress(TimeSpan.FromSeconds(4), 4096, 1600);

        recorder.RaiseExit(0);

        await RecordingManagerHarness.WaitUntilAsync(
            () => harness.Host.Finalized.Count == 1,
            timeoutMilliseconds: 8000,
            message: "等待监控循环自然收尾超时。");

        await worker.FinalizeAsync(requestedStop: true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(harness.Host.Finalized);
        Assert.Single(harness.Merger.Batches);
        Assert.Equal(1, recorder.DisposeCount);
        Assert.Equal(RecordingStatus.Completed, worker.Session.Status);
    }

    [Fact]
    public async Task FinalizeAsync_WhenMergeFails_KeepsCompletedStatusWithMergeNote()
    {
        await using var harness = new WorkerHarness();
        harness.Merger.Result = new SegmentMergeResult(false, 8192, "concat boom", TimeSpan.Zero);
        var worker = harness.CreateWorker();
        await worker.StartAsync();

        var recorder = harness.Recorders[0];
        recorder.RaiseAnchor(harness.Clock.Now);

        var session = await worker.FinalizeAsync(requestedStop: true);

        Assert.Equal(RecordingStatus.Completed, session.Status);
        Assert.Contains("录制分片合并失败", session.ErrorMessage);
        Assert.Contains("concat boom", session.ErrorMessage);
        Assert.Equal(8192L, session.FileSizeBytes);
    }

    [Fact]
    public async Task CleanupStartFailureAsync_MarksFailedAndDeletesArtifacts()
    {
        await using var harness = new WorkerHarness();
        var worker = harness.CreateWorker(configureRecorder: recorder =>
        {
            recorder.CreateOutputFile = true;
            recorder.StartHook = () => Task.FromException(new InvalidOperationException("启动失败（测试注入）"));
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync());
        await worker.CleanupStartFailureAsync(error);

        Assert.Equal(RecordingStatus.Failed, worker.Session.Status);
        Assert.Contains("启动录制失败", worker.Session.ErrorMessage);
        Assert.Contains("启动失败（测试注入）", worker.Session.ErrorMessage);
        Assert.False(File.Exists(worker.Session.VideoFilePath));
        Assert.False(File.Exists(Path.Combine(harness.OutputDirectory, "test.part001.mkv")));
        Assert.Equal(1, harness.Recorders[0].DisposeCount);
        Assert.Equal(1, harness.Host.RemovedActiveCount);
        Assert.Single(harness.Host.PersistOperations, operation => operation == "标记录制启动失败");
    }

    /// <summary>worker 的最小宿主替身：记录回调顺序，可配置主播是否已下播。</summary>
    private sealed class FakeRecordingSessionWorkerHost : IRecordingSessionWorkerHost
    {
        private readonly object _sync = new();
        private readonly List<string> _events = new();
        private readonly List<string> _persistOperations = new();
        private readonly List<RecordingProgress> _progresses = new();
        private readonly List<RecordingProgress> _finalized = new();
        private readonly List<bool> _finalizedUserStops = new();
        private readonly List<LiveRoom> _offlineProbes = new();
        private readonly List<LiveRoom> _retryRooms = new();
        private readonly List<string> _logs = new();
        private readonly List<string> _statusMessages = new();
        private int _removedActiveCount;
        private int _finalizeFailedCount;

        public bool RoomOffline { get; set; } = true;

        public List<string> Events
        {
            get
            {
                lock (_sync)
                {
                    return _events.ToList();
                }
            }
        }

        public List<string> PersistOperations
        {
            get
            {
                lock (_sync)
                {
                    return _persistOperations.ToList();
                }
            }
        }

        public List<RecordingProgress> Progresses
        {
            get
            {
                lock (_sync)
                {
                    return _progresses.ToList();
                }
            }
        }

        public List<RecordingProgress> Finalized
        {
            get
            {
                lock (_sync)
                {
                    return _finalized.ToList();
                }
            }
        }

        public List<bool> FinalizedUserStops
        {
            get
            {
                lock (_sync)
                {
                    return _finalizedUserStops.ToList();
                }
            }
        }

        public List<LiveRoom> OfflineProbes
        {
            get
            {
                lock (_sync)
                {
                    return _offlineProbes.ToList();
                }
            }
        }

        public List<LiveRoom> RetryRooms
        {
            get
            {
                lock (_sync)
                {
                    return _retryRooms.ToList();
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

        public int RemovedActiveCount => Volatile.Read(ref _removedActiveCount);

        public int FinalizeFailedCount => Volatile.Read(ref _finalizeFailedCount);

        public Task PersistSessionAsync(RecordingSession session, string operation)
        {
            lock (_sync)
            {
                _persistOperations.Add(operation);
                _events.Add("persist:" + operation);
            }

            return Task.CompletedTask;
        }

        public void RemoveActive(RecordingSessionWorker worker)
        {
            Interlocked.Increment(ref _removedActiveCount);
            lock (_sync)
            {
                _events.Add("remove");
            }
        }

        public void OnSessionFinalized(RecordingSessionWorker worker, RecordingProgress progress, bool userStop)
        {
            lock (_sync)
            {
                _finalized.Add(progress);
                _finalizedUserStops.Add(userStop);
                _events.Add("finalized");
            }
        }

        public void OnSessionFinalizeFailed(RecordingSessionWorker worker, Exception exception)
        {
            Interlocked.Increment(ref _finalizeFailedCount);
            lock (_sync)
            {
                _events.Add("finalize-failed");
            }
        }

        public void OnProgress(RecordingSessionWorker worker, RecordingProgress progress)
        {
            lock (_sync)
            {
                _progresses.Add(progress);
                _events.Add("progress");
            }
        }

        public void Log(string message)
        {
            lock (_sync)
            {
                _logs.Add(message);
            }
        }

        public void NotifyStatus(string message)
        {
            lock (_sync)
            {
                _statusMessages.Add(message);
            }
        }

        public Task<bool> IsRoomOfflineAsync(LiveRoom room)
        {
            lock (_sync)
            {
                _offlineProbes.Add(room);
            }

            return Task.FromResult(RoomOffline);
        }

        public Task MaybeScheduleRetryAsync(LiveRoom room)
        {
            lock (_sync)
            {
                _retryRooms.Add(room);
                _events.Add("retry");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>直接组装 worker 的夹具：真实状态机 + fake 录制器/合并器/宿主/时钟。</summary>
    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly List<FakeFullStreamRecorder> _recorders = new();
        private readonly List<RecordingSessionWorker> _workers = new();

        public WorkerHarness()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                "LiveMonitor.Media.Tests",
                Guid.NewGuid().ToString("N"));
            OutputDirectory = Path.Combine(RootDirectory, "output");
            Directory.CreateDirectory(OutputDirectory);

            Room = new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = 1,
                Platform = PlatformType.Bilibili,
                RoomId = "1001",
                RawUrl = "https://live.example.com/1001",
                Alias = "测试主播",
                CreatedAt = Clock.Now.UtcDateTime,
            };
        }

        public string RootDirectory { get; }

        public string OutputDirectory { get; }

        public LiveRoom Room { get; }

        public TestClock Clock { get; } = new();

        public FakePlatformProvider Provider { get; } = new();

        public FakeSegmentMerger Merger { get; } = new();

        public RecordingHighlightTracker Tracker { get; } = new();

        public FakeRecordingSessionWorkerHost Host { get; } = new();

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

        public void SetLive() => Provider.Status = new RoomStatus(
            "1001",
            Provider.Platform,
            "测试直播间标题",
            "测试主播",
            true,
            new[] { RecordingManagerHarness.Quality("1080p60", 1080, 60) });

        public RecordingSessionWorker CreateWorker(
            string name = "test",
            Action<FakeFullStreamRecorder>? configureRecorder = null)
        {
            FakeFullStreamRecorder recorder;
            lock (_sync)
            {
                recorder = new FakeFullStreamRecorder();
                configureRecorder?.Invoke(recorder);
                _recorders.Add(recorder);
            }

            var session = new RecordingSession
            {
                Id = Guid.NewGuid(),
                LiveRoomId = Room.Id,
                Platform = Room.Platform,
                RoomId = Room.RoomId,
                RoomDisplayName = RecordingManager.FormatRoomDisplayName(Room),
                StreamTitle = "测试直播间标题",
                Status = RecordingStatus.Starting,
                VideoFilePath = Path.Combine(OutputDirectory, name + ".mkv"),
                DanmakuFilePath = string.Empty,
                StartedAt = Clock.Now.UtcDateTime,
            };

            var context = new RecordingSessionWorkerContext(
                Host,
                Tracker,
                danmaku: null,
                CreateRecorder,
                Merger,
                Clock.Read,
                (delay, ct) => delay >= TimeSpan.FromSeconds(1)
                    ? Task.CompletedTask
                    : Task.Delay(delay, ct),
                TimeSpan.FromMilliseconds(20));

            var worker = new RecordingSessionWorker(
                Room,
                session,
                recorder,
                Provider,
                cookie: null,
                RecordingManagerHarness.Quality("1080p60", 1080, 60),
                Clock.Now,
                context);
            _workers.Add(worker);
            return worker;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var worker in _workers)
            {
                try
                {
                    worker.StopRequested = true;
                    await worker.FinalizeAsync(requestedStop: true).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // 测试清理阶段的收尾失败不应影响断言结果。
                }
            }

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

        private IFullStreamRecorder CreateRecorder()
        {
            lock (_sync)
            {
                var recorder = new FakeFullStreamRecorder();
                _recorders.Add(recorder);
                return recorder;
            }
        }
    }
}
