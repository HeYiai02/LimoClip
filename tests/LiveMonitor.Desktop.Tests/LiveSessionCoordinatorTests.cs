using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 「边录边剪」同步编排测试：用 Fake 定时器/录制器/波形提取器/弹幕读取器与记录调用的 Fake Host，
/// 断言手动/自动同步的固定顺序、10 秒周期、重入保护、分段停止、录制结束切离线与失败文案。
/// </summary>
public sealed class LiveSessionCoordinatorTests
{
    [Fact]
    public void StartAutoSync_starts_timer_with_ten_second_interval()
    {
        using var context = new TestContext();

        context.Coordinator.StartAutoSync();

        Assert.Equal(TimeSpan.FromSeconds(10), context.Timer.Interval);
        Assert.True(context.Timer.IsRunning);
    }

    [Fact]
    public void StartAutoSync_ignores_inactive_session()
    {
        using var context = new TestContext();
        context.Fixture.Live.Reset();

        context.Coordinator.StartAutoSync();

        Assert.False(context.Timer.IsRunning);
    }

    [Fact]
    public async Task AutoSync_tick_refreshes_progress_danmaku_and_highlights_without_player_reload()
    {
        using var context = new TestContext();
        context.Reader.Results.Enqueue(new IncrementalDanmakuReadResult(
            new[] { Danmaku(offsetMs: 1000) },
            NextByteOffset: 64));
        context.Coordinator.StartAutoSync();

        await context.Timer.FireAsync();

        Assert.Equal(
            new[]
            {
                "syncing-on", "progress", "danmaku", "highlights", "waveform", "timeline", "status", "syncing-off"
            },
            context.Host.Calls);
        Assert.Single(context.Host.AppliedProgress);
        Assert.Single(context.Host.AppliedDanmaku);
        Assert.Equal(64, context.Fixture.Live.DanmakuOffset);
        Assert.Equal(0, context.Host.PlayerReloadRequested);
        Assert.Empty(context.Extractor.Calls);
        Assert.Equal(0, context.Host.WaveformLoadingOffCount);
        Assert.Equal(context.SafeClipDuration, context.Host.SafeClipDuration);
        Assert.Equal(
            ClipStudioStatusText.LiveSyncSucceeded(context.Host.LiveRecordedDuration, context.Host.SafeClipDuration),
            context.Host.Status);
    }

    [Fact]
    public async Task ManualSync_reloads_player_and_extracts_incremental_waveform()
    {
        using var context = new TestContext();

        await context.Coordinator.SyncManualAsync();

        Assert.Equal(
            new[]
            {
                "syncing-on", "progress", "highlights",
                "waveform", "timeline", "waveform-loading-off",
                "waveform", "timeline", "reload", "status", "syncing-off"
            },
            context.Host.Calls);
        var call = Assert.Single(context.Extractor.Calls);
        Assert.Equal(TimeSpan.Zero, call.From);
        Assert.True(call.To > TimeSpan.Zero);
        Assert.Equal(call.To, context.Fixture.Live.PeaksExtractedUntil);
        Assert.Equal(1, context.Host.PlayerReloadRequested);
        Assert.Equal(1, context.Host.WaveformLoadingOffCount);
    }

    [Fact]
    public async Task Sync_when_recording_finished_switches_to_offline_without_reloading_player()
    {
        using var context = new TestContext();
        context.Monitor.Live = null;

        await context.Coordinator.SyncManualAsync();

        Assert.Equal(new[] { "syncing-on", "completed", "syncing-off" }, context.Host.Calls);
        Assert.Equal(1, context.Host.RecordingCompletedCalls);
        Assert.Empty(context.Host.AppliedProgress);
        Assert.Equal(0, context.Host.PlayerReloadRequested);
    }

    [Fact]
    public async Task Sync_when_second_part_started_stops_live_mode()
    {
        using var context = new TestContext();
        context.Monitor.Live = context.Fixture.Info with { PartNumber = 2 };

        await context.Coordinator.SyncManualAsync();

        Assert.Equal(new[] { "syncing-on", "segmented", "syncing-off" }, context.Host.Calls);
        Assert.Equal(1, context.Host.SegmentedStopCount);
        Assert.Equal(2, context.Host.SegmentedStopPartNumber);
        Assert.Equal(ClipStudioStatusText.LiveSyncStoppedBySegment(2), context.Host.Status);
        Assert.Empty(context.Host.AppliedProgress);
    }

    [Fact]
    public async Task AutoSync_skips_when_disabled_or_not_live()
    {
        using var context = new TestContext();
        context.Host.IsAutoSyncEnabled = false;
        context.Coordinator.StartAutoSync();

        await context.Timer.FireAsync();
        Assert.Empty(context.Host.Calls);

        context.Host.IsAutoSyncEnabled = true;
        context.Fixture.Live.Reset();
        await context.Timer.FireAsync();
        Assert.Empty(context.Host.Calls);
    }

    [Fact]
    public async Task Manual_sync_failure_reports_sync_failed_status()
    {
        using var context = new TestContext();
        context.Host.ApplyProgressError = new InvalidOperationException("boom");

        await context.Coordinator.SyncManualAsync();

        Assert.Equal(ClipStudioStatusText.LiveSyncFailed("boom"), context.Host.Status);
        Assert.Equal(new[] { "syncing-on", "progress", "status", "syncing-off" }, context.Host.Calls);
    }

    [Fact]
    public async Task Auto_sync_failure_reports_auto_sync_failed_status()
    {
        using var context = new TestContext();
        context.Host.ApplyProgressError = new InvalidOperationException("boom");
        context.Coordinator.StartAutoSync();

        await context.Timer.FireAsync();

        Assert.Equal(ClipStudioStatusText.AutoSyncFailed("boom"), context.Host.Status);
    }

    [Fact]
    public async Task Second_sync_is_ignored_while_first_is_running()
    {
        using var context = new TestContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Host.HighlightsGate = gate;

        var manual = context.Coordinator.SyncManualAsync();
        var auto = context.Coordinator.AutoSyncAsync();
        gate.SetResult();
        await Task.WhenAll(manual, auto);

        Assert.Single(context.Host.AppliedProgress);
        Assert.Equal(new[] { true, false }, context.Host.SyncingValues);
    }

    [Fact]
    public async Task RefreshDanmaku_appends_new_items_and_advances_offset()
    {
        using var context = new TestContext();
        context.Reader.Results.Enqueue(new IncrementalDanmakuReadResult(
            new[] { Danmaku(500), Danmaku(900) },
            NextByteOffset: 128));

        await context.Coordinator.RefreshDanmakuAsync(CancellationToken.None);
        await context.Coordinator.RefreshDanmakuAsync(CancellationToken.None);

        Assert.Single(context.Host.AppliedDanmaku);
        Assert.Equal(2, context.Host.AppliedDanmaku[0].Count);
        Assert.Equal(128, context.Fixture.Live.DanmakuOffset);
        Assert.Equal(new long[] { 0, 128 }, context.Reader.Offsets);
    }

    [Fact]
    public async Task RefreshDanmaku_ignores_missing_file()
    {
        using var context = new TestContext();
        context.Reader.Error = new FileNotFoundException("missing");

        await context.Coordinator.RefreshDanmakuAsync(CancellationToken.None);

        Assert.Null(context.Host.Status);
        Assert.Empty(context.Host.AppliedDanmaku);
    }

    [Fact]
    public async Task RefreshDanmaku_without_path_does_nothing()
    {
        using var context = new TestContext();
        context.Host.DanmakuPath = null;

        await context.Coordinator.RefreshDanmakuAsync(CancellationToken.None);

        Assert.Empty(context.Reader.Offsets);
        Assert.Empty(context.Host.Calls);
    }

    [Fact]
    public async Task Waveform_extraction_skips_when_no_safe_window_is_available()
    {
        using var context = new TestContext();
        context.Fixture.Live.MarkPeaksExtracted(context.SafeClipDuration);

        await context.Coordinator.ExtractWaveformAsync(CancellationToken.None);

        Assert.Empty(context.Extractor.Calls);
        Assert.DoesNotContain("waveform", context.Host.Calls);
        Assert.Equal(1, context.Host.WaveformLoadingOffCount);
    }

    [Fact]
    public void Dispose_stops_timer()
    {
        var context = new TestContext();
        context.Coordinator.StartAutoSync();

        context.Coordinator.Dispose();

        Assert.False(context.Timer.IsRunning);
        Assert.True(context.Timer.StopCount >= 1);
    }

    private static RecordedDanmakuItem Danmaku(long offsetMs) =>
        new(offsetMs, "测试用户", "测试弹幕", IsSticker: false, IsGift: false);

    private static LiveFixture CreateFixture()
    {
        var session = new RecordingSession
        {
            Platform = PlatformType.Bilibili,
            RoomId = "room-live",
            RoomDisplayName = "直播中房间",
            StartedAt = DateTime.UtcNow,
            Status = RecordingStatus.Recording
        };
        var partPath = Path.Combine(Path.GetTempPath(), "coordinator.part001.mkv");
        var info = new LiveRecordingInfo(
            session.Id,
            Guid.NewGuid(),
            1,
            "直播中房间",
            partPath,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12),
            FileSizeBytes: 1024 * 1024,
            DanmakuCount: 2,
            HighlightCount: 1);
        var live = new LiveClipSession();
        live.Start(info, partPath);
        return new LiveFixture(session, info, live);
    }

    private sealed record LiveFixture(RecordingSession Session, LiveRecordingInfo Info, LiveClipSession Live);

    private sealed class TestContext : IDisposable
    {
        public TestContext()
        {
            Fixture = CreateFixture();
            Host.CurrentSession = Fixture.Session;
            Host.DanmakuPath = "/tmp/coordinator.jsonl";
            Host.LiveRecordedDuration = Fixture.Info.RecordedDuration;
            Host.SafeClipDuration = Fixture.Live.SafeClipDuration;
            Monitor = new FakeMonitor { Live = Fixture.Info };
            Coordinator = new LiveSessionCoordinator(
                Fixture.Live,
                Monitor,
                Extractor,
                Reader,
                Timer,
                Host);
        }

        public LiveFixture Fixture { get; }

        public FakeHost Host { get; } = new();

        public FakeTimer Timer { get; } = new();

        public FakeMonitor Monitor { get; }

        public FakeExtractor Extractor { get; } = new();

        public FakeReader Reader { get; } = new();

        public LiveSessionCoordinator Coordinator { get; }

        public TimeSpan SafeClipDuration => Fixture.Live.SafeClipDuration;

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class FakeHost : ILiveSessionSyncHost
    {
        public bool IsDisposed { get; set; }

        public bool IsAutoSyncEnabled { get; set; } = true;

        public RecordingSession? CurrentSession { get; set; }

        public string? DanmakuPath { get; set; }

        public TimeSpan LiveRecordedDuration { get; set; }

        public TimeSpan SafeClipDuration { get; set; }

        public List<string> Calls { get; } = new();

        public List<LiveRecordingInfo> AppliedProgress { get; } = new();

        public List<IReadOnlyList<RecordedDanmakuItem>> AppliedDanmaku { get; } = new();

        public List<bool> SyncingValues { get; } = new();

        public int PlayerReloadRequested { get; private set; }

        public int WaveformLoadingOffCount { get; private set; }

        public int SegmentedStopCount { get; private set; }

        public int? SegmentedStopPartNumber { get; private set; }

        public int RecordingCompletedCalls { get; private set; }

        public string? Status { get; private set; }

        public Exception? ApplyProgressError { get; set; }

        public TaskCompletionSource? HighlightsGate { get; set; }

        public void ApplyProgress(LiveRecordingInfo latest)
        {
            Calls.Add("progress");
            if (ApplyProgressError is not null)
            {
                throw ApplyProgressError;
            }

            AppliedProgress.Add(latest);
        }

        public void ApplyDanmaku(IReadOnlyList<RecordedDanmakuItem> items)
        {
            Calls.Add("danmaku");
            AppliedDanmaku.Add(items);
        }

        public void ApplyWaveform() => Calls.Add("waveform");

        public void SetWaveformLoading(bool loading)
        {
            Calls.Add(loading ? "waveform-loading" : "waveform-loading-off");
            if (!loading)
            {
                WaveformLoadingOffCount++;
            }
        }

        public async Task ReloadHighlightsAsync(TimeSpan duration, CancellationToken ct)
        {
            Calls.Add("highlights");
            if (HighlightsGate is { } gate)
            {
                await gate.Task.ConfigureAwait(true);
            }
        }

        public void RequestPlayerReload()
        {
            Calls.Add("reload");
            PlayerReloadRequested++;
        }

        public void RaiseTimelineChanged() => Calls.Add("timeline");

        public void SetStatus(string text)
        {
            Calls.Add("status");
            Status = text;
        }

        public void SetSyncing(bool syncing)
        {
            Calls.Add(syncing ? "syncing-on" : "syncing-off");
            SyncingValues.Add(syncing);
        }

        public void StopForSegmentedRecording(int partNumber)
        {
            Calls.Add("segmented");
            SegmentedStopCount++;
            SegmentedStopPartNumber = partNumber;
            Status = ClipStudioStatusText.LiveSyncStoppedBySegment(partNumber);
        }

        public Task HandleRecordingCompletedAsync()
        {
            Calls.Add("completed");
            RecordingCompletedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimer : ILiveSessionTimer
    {
        public TimeSpan? Interval { get; private set; }

        public Func<Task>? Tick { get; private set; }

        public bool IsRunning { get; private set; }

        public int StopCount { get; private set; }

        public void Start(TimeSpan interval, Func<Task> tick)
        {
            Interval = interval;
            Tick = tick;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            StopCount++;
        }

        public Task FireAsync() => IsRunning && Tick is { } tick ? tick() : Task.CompletedTask;
    }

    private sealed class FakeMonitor : ILiveRecordingMonitor
    {
        public LiveRecordingInfo? Live { get; set; }

        public IReadOnlyList<LiveRecordingInfo> GetLiveRecordings() =>
            Live is null ? Array.Empty<LiveRecordingInfo>() : new[] { Live };

        public bool TryGetLiveRecording(Guid sessionId, out LiveRecordingInfo info)
        {
            if (Live is not null && Live.SessionId == sessionId)
            {
                info = Live;
                return true;
            }

            info = null!;
            return false;
        }

        public event Action<RecordingSession>? SessionCompleted
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeExtractor : ILiveWaveformExtractor
    {
        public List<(TimeSpan From, TimeSpan To)> Calls { get; } = new();

        public Task<float[]> ExtractPeaksAsync(
            string videoPath,
            TimeSpan fromTime,
            TimeSpan toTime,
            int peaksPerSecond = 2,
            CancellationToken ct = default)
        {
            Calls.Add((fromTime, toTime));
            var count = Math.Max(0, (int)Math.Ceiling((toTime - fromTime).TotalSeconds * peaksPerSecond));
            return Task.FromResult(new float[count]);
        }
    }

    private sealed class FakeReader : IIncrementalDanmakuReader
    {
        public Queue<IncrementalDanmakuReadResult> Results { get; } = new();

        public List<long> Offsets { get; } = new();

        public Exception? Error { get; set; }

        public Task<IncrementalDanmakuReadResult> ReadIncrementalAsync(
            string jsonlPath,
            long fromByteOffset,
            CancellationToken ct)
        {
            Offsets.Add(fromByteOffset);
            if (Error is not null)
            {
                throw Error;
            }

            var result = Results.Count > 0
                ? Results.Dequeue()
                : new IncrementalDanmakuReadResult(Array.Empty<RecordedDanmakuItem>(), fromByteOffset);
            return Task.FromResult(result);
        }
    }
}
