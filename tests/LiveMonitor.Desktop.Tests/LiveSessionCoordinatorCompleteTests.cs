using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 「录制结束切离线」编排测试：非 live / 场次不一致时忽略、切离线的固定顺序、缺文件与异常文案、
/// 选区恢复门槛、宿主释放后中止、弹幕文件不存在时透传 null，以及取消时静默。
/// 录制器/波形/弹幕读取/定时器均由 Stub 承接，不触碰真实录制文件或 UI。
/// </summary>
public sealed class LiveSessionCoordinatorCompleteTests
{
    [Fact]
    public async Task CompleteAsync_ignores_session_when_not_live()
    {
        using var context = new CompleteContext(startLive: false);

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Empty(context.Completion.Calls);
        Assert.Null(context.Completion.Status);
    }

    [Fact]
    public async Task CompleteAsync_ignores_foreign_session_id()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.CurrentSessionId = Guid.NewGuid();

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Empty(context.Completion.Calls);
        Assert.Null(context.Completion.Status);
    }

    [Fact]
    public async Task CompleteAsync_switches_to_offline_in_fixed_order()
    {
        using var context = new CompleteContext(startLive: true);

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(
            new[] { "exit", "load", "open-offline", "refresh", "selection", "seek", "timeline", "status" },
            context.Completion.Calls);
        Assert.Equal(context.VideoPath, context.Completion.OpenedVideoPath);
        Assert.Equal(context.DanmakuPath, context.Completion.OpenedDanmakuPath);
        Assert.Equal(context.Session.Id, context.Completion.OpenedSession?.Id);
        Assert.Equal(TimeSpan.FromSeconds(30), context.Completion.SoughtPosition);
        Assert.Equal(TimeSpan.FromSeconds(5), context.Completion.RestoredInPoint);
        Assert.Equal(TimeSpan.FromSeconds(15), context.Completion.RestoredOutPoint);
        Assert.Equal(ClipStudioStatusText.LiveCompleted, context.Completion.Status);
        Assert.False(context.Timer.IsRunning);
    }

    [Fact]
    public async Task CompleteAsync_reports_without_file_and_skips_offline_open()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.SessionToLoad = new RecordingSession
        {
            Platform = PlatformType.Bilibili,
            RoomId = "room-complete",
            RoomDisplayName = "结束测试房间",
            Status = RecordingStatus.Completed,
            ErrorMessage = "录制未产生文件"
        };

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(new[] { "exit", "load", "status" }, context.Completion.Calls);
        Assert.Equal(ClipStudioStatusText.LiveCompletedWithoutFile("录制未产生文件"), context.Completion.Status);
        Assert.Null(context.Completion.OpenedVideoPath);
    }

    [Fact]
    public async Task CompleteAsync_reports_switch_failure_when_offline_open_throws()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.OpenOfflineError = new IOException("文件被占用");

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(new[] { "exit", "load", "open-offline", "status" }, context.Completion.Calls);
        Assert.Equal(ClipStudioStatusText.LiveSwitchToOfflineFailed("文件被占用"), context.Completion.Status);
    }

    [Fact]
    public async Task CompleteAsync_skips_selection_restore_when_range_is_too_short()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.LastInPoint = TimeSpan.FromSeconds(10);
        context.Completion.LastOutPoint = TimeSpan.FromSeconds(10.1);

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.DoesNotContain("selection", context.Completion.Calls);
        Assert.Contains("seek", context.Completion.Calls);
        Assert.Equal(ClipStudioStatusText.LiveCompleted, context.Completion.Status);
    }

    [Fact]
    public async Task CompleteAsync_aborts_when_host_becomes_disposed_after_loading()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.DisposeOnLoad = true;

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(new[] { "exit", "load" }, context.Completion.Calls);
        Assert.Null(context.Completion.Status);
    }

    [Fact]
    public async Task CompleteAsync_passes_null_danmaku_when_file_is_missing()
    {
        using var context = new CompleteContext(startLive: true, createDanmaku: false);

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(1, context.Completion.OfflineOpenCount);
        Assert.Null(context.Completion.OpenedDanmakuPath);
    }

    [Fact]
    public async Task CompleteAsync_returns_silently_when_cancelled()
    {
        using var context = new CompleteContext(startLive: true);
        context.Completion.LoadError = new OperationCanceledException();

        await context.Coordinator.CompleteAsync(context.Session);

        Assert.Equal(new[] { "exit", "load" }, context.Completion.Calls);
        Assert.Null(context.Completion.Status);
    }

    private sealed class CompleteContext : IDisposable
    {
        public CompleteContext(bool startLive, bool createDanmaku = true)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "limoclip-complete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            VideoPath = Path.Combine(TempDirectory, "session.mkv");
            File.WriteAllBytes(VideoPath, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
            DanmakuPath = Path.Combine(TempDirectory, "session.jsonl");
            if (createDanmaku)
            {
                File.WriteAllText(DanmakuPath, "{\"t\":0}\n");
            }

            Session = new RecordingSession
            {
                Platform = PlatformType.Bilibili,
                RoomId = "room-complete",
                RoomDisplayName = "结束测试房间",
                DanmakuFilePath = createDanmaku ? DanmakuPath : string.Empty,
                Status = RecordingStatus.Recording
            };
            Info = new LiveRecordingInfo(
                Session.Id,
                Guid.NewGuid(),
                RoomDisplayNumber: 1,
                RoomDisplayName: "结束测试房间",
                PartFilePath: VideoPath,
                PartNumber: 1,
                CompletedDuration: TimeSpan.Zero,
                RecordedDuration: TimeSpan.FromSeconds(40),
                FileSizeBytes: 1024 * 1024,
                DanmakuCount: 2,
                HighlightCount: 1);

            Live = new LiveClipSession();
            if (startLive)
            {
                Live.Start(Info, VideoPath);
            }

            Completion = new FakeCompletionHost
            {
                CurrentSessionId = Session.Id,
                LastPosition = TimeSpan.FromSeconds(30),
                LastInPoint = TimeSpan.FromSeconds(5),
                LastOutPoint = TimeSpan.FromSeconds(15),
                SessionToLoad = new RecordingSession
                {
                    Platform = PlatformType.Bilibili,
                    RoomId = "room-complete",
                    RoomDisplayName = "结束测试房间",
                    VideoFilePath = VideoPath,
                    DanmakuFilePath = createDanmaku ? DanmakuPath : string.Empty,
                    Status = RecordingStatus.Completed
                }
            };
            Completion.SessionToLoad.Id = Session.Id;

            Coordinator = new LiveSessionCoordinator(
                Live,
                new FakeMonitor(),
                new RealExtractorStub(),
                new FakeReader(),
                Timer,
                new StubSyncHost { CurrentSession = Session },
                openHost: null,
                completionHost: Completion);
        }

        public string TempDirectory { get; }

        public string VideoPath { get; }

        public string DanmakuPath { get; }

        public RecordingSession Session { get; }

        public LiveRecordingInfo Info { get; }

        public LiveClipSession Live { get; }

        public FakeCompletionHost Completion { get; }

        public FakeTimer Timer { get; } = new();

        public LiveSessionCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>记录切离线回调顺序与入参的 Fake 宿主。</summary>
    private sealed class FakeCompletionHost : ILiveSessionCompletionHost
    {
        public List<string> Calls { get; } = new();

        public string? Status { get; private set; }

        public Guid? CurrentSessionId { get; set; }

        public TimeSpan LastPosition { get; set; }

        public TimeSpan LastInPoint { get; set; }

        public TimeSpan LastOutPoint { get; set; }

        public bool IsDisposed { get; private set; }

        public bool DisposeOnLoad { get; set; }

        public RecordingSession? SessionToLoad { get; set; }

        public Exception? LoadError { get; set; }

        public Exception? OpenOfflineError { get; set; }

        public string? OpenedVideoPath { get; private set; }

        public string? OpenedDanmakuPath { get; private set; }

        public RecordingSession? OpenedSession { get; private set; }

        public int OfflineOpenCount { get; private set; }

        public TimeSpan SoughtPosition { get; private set; }

        public TimeSpan RestoredInPoint { get; private set; }

        public TimeSpan RestoredOutPoint { get; private set; }

        public void ExitLiveMode() => Calls.Add("exit");

        public Task<RecordingSession?> LoadSessionAsync(CancellationToken ct)
        {
            Calls.Add("load");
            if (DisposeOnLoad)
            {
                IsDisposed = true;
            }

            if (LoadError is not null)
            {
                return Task.FromException<RecordingSession?>(LoadError);
            }

            return Task.FromResult(SessionToLoad);
        }

        public Task OpenOfflineAsync(string videoPath, string? danmakuPath, RecordingSession session, CancellationToken ct)
        {
            Calls.Add("open-offline");
            OfflineOpenCount++;
            OpenedVideoPath = videoPath;
            OpenedDanmakuPath = danmakuPath;
            OpenedSession = session;
            return OpenOfflineError is null ? Task.CompletedTask : Task.FromException(OpenOfflineError);
        }

        public Task RefreshSessionsAsync(CancellationToken ct)
        {
            Calls.Add("refresh");
            return Task.CompletedTask;
        }

        public void RestoreSelection(TimeSpan inPoint, TimeSpan outPoint)
        {
            Calls.Add("selection");
            RestoredInPoint = inPoint;
            RestoredOutPoint = outPoint;
        }

        public void SeekTo(TimeSpan position)
        {
            Calls.Add("seek");
            SoughtPosition = position;
        }

        public void RaiseTimelineChanged() => Calls.Add("timeline");

        public void SetStatus(string text)
        {
            Calls.Add("status");
            Status = text;
        }
    }

    /// <summary>只记录状态的最小同步宿主（本测试不触发同步）。</summary>
    private sealed class StubSyncHost : ILiveSessionSyncHost
    {
        public bool IsDisposed { get; set; }

        public bool IsAutoSyncEnabled { get; set; } = true;

        public RecordingSession? CurrentSession { get; set; }

        public string? DanmakuPath { get; set; }

        public TimeSpan LiveRecordedDuration { get; set; }

        public TimeSpan SafeClipDuration { get; set; }

        public void ApplyProgress(LiveRecordingInfo latest)
        {
        }

        public void ApplyDanmaku(IReadOnlyList<RecordedDanmakuItem> items)
        {
        }

        public void ApplyWaveform()
        {
        }

        public void SetWaveformLoading(bool loading)
        {
        }

        public Task ReloadHighlightsAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

        public void RequestPlayerReload()
        {
        }

        public void RaiseTimelineChanged()
        {
        }

        public void SetStatus(string text)
        {
        }

        public void SetSyncing(bool syncing)
        {
        }

        public void StopForSegmentedRecording(int partNumber)
        {
        }

        public Task HandleRecordingCompletedAsync() => Task.CompletedTask;
    }

    /// <summary>录制器 Stub：本测试不需要查找正在录制的会话。</summary>
    private sealed class FakeMonitor : ILiveRecordingMonitor
    {
        public event Action<RecordingSession>? SessionCompleted
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LiveRecordingInfo> GetLiveRecordings() => Array.Empty<LiveRecordingInfo>();

        public bool TryGetLiveRecording(Guid sessionId, out LiveRecordingInfo info)
        {
            info = null!;
            return false;
        }
    }

    /// <summary>波形提取器占位：本测试不触发提取。</summary>
    private sealed class RealExtractorStub : ILiveWaveformExtractor
    {
        public Task<float[]> ExtractPeaksAsync(
            string videoPath,
            TimeSpan fromTime,
            TimeSpan toTime,
            int peaksPerSecond = 2,
            CancellationToken ct = default)
            => Task.FromResult(Array.Empty<float>());
    }

    /// <summary>弹幕增量读取占位：本测试不触发读取。</summary>
    private sealed class FakeReader : IIncrementalDanmakuReader
    {
        public Task<IncrementalDanmakuReadResult> ReadIncrementalAsync(
            string jsonlPath,
            long fromByteOffset,
            CancellationToken ct)
            => Task.FromResult(new IncrementalDanmakuReadResult(
                Array.Empty<RecordedDanmakuItem>(),
                fromByteOffset));
    }

    private sealed class FakeTimer : ILiveSessionTimer
    {
        public bool IsRunning { get; private set; }

        public TimeSpan? Interval { get; private set; }

        public void Start(TimeSpan interval, Func<Task> tick)
        {
            IsRunning = true;
            Interval = interval;
        }

        public void Stop() => IsRunning = false;
    }
}
