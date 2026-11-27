using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// RecordingTargetPoller 的单元测试：只验证「轮询节拍 / 并发上限 / 探测异常与取消 / 抑制判定时机」
/// 这些从 RecordingManager 抽离出来的行为，起录本身由宿主回调（FakePollerHost）替代。
/// </summary>
public sealed class RecordingTargetPollerTests
{
    private static readonly StreamQuality OriginalQuality =
        new("1080p60", "1080p60", 1080, 60, "https://example.com/live.flv", new Dictionary<string, string>());

    [Fact]
    public async Task PollOnceAsync_LiveRoom_InvokesStartCallback()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: true);
        context.Provider.Status = LiveStatus(room);

        await using var poller = context.CreatePoller();

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { room.Id }, context.Host.StartedRoomIds);
        Assert.Equal(1, context.Provider.StatusCalls);
        Assert.DoesNotContain(context.Host.Logs, log => log.Contains("失败"));
    }

    [Fact]
    public async Task PollOnceAsync_OfflineRoom_ResetsPollingStateWithoutStarting()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: true);
        context.Host.Suppress(room.Id);

        await using var poller = context.CreatePoller();

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(room.Id, context.Host.ResetRoomIds);
        Assert.Empty(context.Host.StartedRoomIds);
        Assert.False(context.Host.IsAutoRestartSuppressed(room.Id));
    }

    [Fact]
    public async Task PollOnceAsync_SuppressedLiveRoom_DoesNotStartOrClearSuppression()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: true);
        context.Provider.Status = LiveStatus(room);
        context.Host.Suppress(room.Id);

        await using var poller = context.CreatePoller();

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(context.Host.StartedRoomIds);
        Assert.Empty(context.Host.ResetRoomIds);
        Assert.True(context.Host.IsAutoRestartSuppressed(room.Id));
        Assert.Equal(1, context.Provider.StatusCalls);
    }

    [Fact]
    public async Task PollOnceAsync_AlreadyRecordingRoom_SkipsStatusProbe()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: true);
        context.Provider.Status = LiveStatus(room);
        context.Host.MarkRecording(room.Id);

        await using var poller = context.CreatePoller();

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(context.Host.StartedRoomIds);
        Assert.Equal(0, context.Provider.StatusCalls);
    }

    [Fact]
    public async Task PollOnceAsync_ConcurrentPollers_RespectsConcurrencyLimit()
    {
        var context = new PollerTestContext();
        var rooms = Enumerable.Range(0, 8)
            .Select(index => AddRoom(context, $"2{index:00}", displayNumber: index + 1))
            .ToList();
        foreach (var room in rooms)
        {
            context.Targets.Set(room.Id, autoRecordEnabled: true);
        }

        context.Provider.Status = LiveStatus(rooms[0]);

        await using var poller = context.CreatePoller();

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var peak = 0;
        context.Provider.BeforeStatusAsync = async () =>
        {
            var concurrent = Interlocked.Increment(ref current);
            RecordPeak(ref peak, concurrent);
            if (concurrent >= RecordingTargetPoller.MaxConcurrentPolls)
            {
                saturated.TrySetResult(true);
            }

            try
            {
                await release.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        };

        var polls = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => poller.PollOnceAsync()))
            .ToArray();

        await saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        Assert.Equal(4, RecordingTargetPoller.MaxConcurrentPolls);
        Assert.Equal(RecordingTargetPoller.MaxConcurrentPolls, Volatile.Read(ref peak));

        release.TrySetResult(true);
        await Task.WhenAll(polls).WaitAsync(TimeSpan.FromSeconds(20));

        var startedRoomIds = context.Host.StartedRoomIds.Distinct().ToList();
        Assert.Equal(rooms.Count, startedRoomIds.Count);
        Assert.All(rooms, room => Assert.Contains(room.Id, startedRoomIds));
    }

    [Fact]
    public async Task PollOnceAsync_ReadRepositoriesFails_LogsAndSkipsRound()
    {
        var context = new PollerTestContext();
        await using var poller = new RecordingTargetPoller(
            new ThrowingRoomRepository(),
            context.Targets,
            context.Settings,
            platform => context.Provider,
            (_, _) => Task.FromResult<string?>(null),
            context.Host,
            TimeSpan.FromSeconds(30));

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(context.Host.Logs, log => log.Contains("读取直播间/录制偏好失败"));
        Assert.Empty(context.Host.StartedRoomIds);
        Assert.Equal(0, context.Provider.StatusCalls);
    }

    [Fact]
    public async Task PollOnceAsync_StatusProbeThrows_LogsAndReleasesConcurrencyGate()
    {
        var context = new PollerTestContext();
        var rooms = Enumerable.Range(0, 5)
            .Select(index => AddRoom(context, $"3{index:00}", displayNumber: index + 1))
            .ToList();
        foreach (var room in rooms)
        {
            context.Targets.Set(room.Id, autoRecordEnabled: true);
        }

        context.Provider.StatusException = new InvalidOperationException("平台接口失败");

        await using var poller = context.CreatePoller();

        // 探测异常必须被轮询器吞噬（只记录日志），且 finally 释放并发闸门，不能卡死后续探测。
        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            rooms.Count,
            context.Host.Logs.Count(log => log.Contains("自动录制检查失败")));
        Assert.Equal(rooms.Count, context.Host.StatusMessages.Count(message => message.Contains("自动录制失败")));
        Assert.Empty(context.Host.StartedRoomIds);

        context.Provider.StatusException = null;
        context.Provider.Status = LiveStatus(rooms[0]);

        await poller.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(rooms.Count, context.Host.StartedRoomIds.Count);
    }

    [Fact]
    public async Task Start_PollsAfterInitialDelayThenAtConfiguredInterval_AndStopsOnCancel()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: true);
        context.Provider.Status = LiveStatus(room);

        var configuredInterval = TimeSpan.FromSeconds(42);
        var delaySpans = new List<TimeSpan>();
        var delayGate = new SemaphoreSlim(0);
        context.Delay = async (span, token) =>
        {
            lock (delaySpans)
            {
                delaySpans.Add(span);
            }

            await delayGate.WaitAsync(token).ConfigureAwait(false);
        };

        await using var poller = context.CreatePoller(pollIntervalOverride: configuredInterval);
        await poller.ConfigureAsync();
        Assert.Equal(configuredInterval, poller.PollInterval);

        using var cts = new CancellationTokenSource();
        poller.Start(cts.Token);

        try
        {
            await RecordingManagerHarness.WaitUntilAsync(() => DelayCount(delaySpans) >= 1, timeoutMilliseconds: 20000);
            Assert.Equal(TimeSpan.FromSeconds(3), RecordingTargetPoller.InitialPollDelay);
            Assert.Equal(RecordingTargetPoller.InitialPollDelay, DelayAt(delaySpans, 0));

            delayGate.Release();
            await RecordingManagerHarness.WaitUntilAsync(() => DelayCount(delaySpans) >= 2, timeoutMilliseconds: 20000);
            delayGate.Release();
            await RecordingManagerHarness.WaitUntilAsync(() => DelayCount(delaySpans) >= 3, timeoutMilliseconds: 20000);

            Assert.Equal(configuredInterval, DelayAt(delaySpans, 1));
            Assert.Equal(configuredInterval, DelayAt(delaySpans, 2));
            Assert.True(context.Provider.StatusCalls >= 1);
        }
        finally
        {
            cts.Cancel();
            await poller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            delayGate.Dispose();
        }
    }

    private static int DelayCount(List<TimeSpan> spans)
    {
        lock (spans)
        {
            return spans.Count;
        }
    }

    private static TimeSpan DelayAt(List<TimeSpan> spans, int index)
    {
        lock (spans)
        {
            return spans[index];
        }
    }

    private static void RecordPeak(ref int peak, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref peak);
            if (value <= observed || Interlocked.CompareExchange(ref peak, value, observed) == observed)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task PollRoomOnceAsync_StartsOnlyTargetRoomWhenLive()
    {
        var context = new PollerTestContext();
        var target = AddRoom(context, "1001");
        var other = AddRoom(context, "1002", displayNumber: 2);
        context.Targets.Set(target.Id, autoRecordEnabled: true);
        context.Targets.Set(other.Id, autoRecordEnabled: true);
        context.Provider.Status = LiveStatus(target);

        await using var poller = context.CreatePoller();

        await poller.PollRoomOnceAsync(target.Id).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { target.Id }, context.Host.StartedRoomIds);
        Assert.Equal(1, context.Provider.StatusCalls);
    }

    [Fact]
    public async Task PollRoomOnceAsync_DisabledTarget_DoesNothing()
    {
        var context = new PollerTestContext();
        var room = AddRoom(context, "1001");
        context.Targets.Set(room.Id, autoRecordEnabled: false);
        context.Provider.Status = LiveStatus(room);

        await using var poller = context.CreatePoller();

        await poller.PollRoomOnceAsync(room.Id).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(context.Host.StartedRoomIds);
        Assert.Equal(0, context.Provider.StatusCalls);
    }

    private static LiveRoom AddRoom(
        PollerTestContext context,
        string roomId,
        int displayNumber = 1,
        PlatformType platform = PlatformType.Bilibili)
    {
        var room = new LiveRoom
        {
            Platform = platform,
            RoomId = roomId,
            RawUrl = $"https://live.example.com/{roomId}",
            Alias = $"主播{roomId}",
            DisplayNumber = displayNumber,
        };
        context.Rooms.Rooms.Add(room);
        return room;
    }

    private static RoomStatus LiveStatus(LiveRoom room)
        => new(room.RoomId, room.Platform, "测试直播间标题", room.Alias, true, new[] { OriginalQuality });

    private sealed class PollerTestContext
    {
        public FakeRoomRepository Rooms { get; } = new();

        public FakeRecordingTargetRepository Targets { get; } = new();

        public FakeAppSettingsRepository Settings { get; } = new();

        public FakePlatformProvider Provider { get; } = new();

        public FakePollerHost Host { get; } = new();

        public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

        public RecordingTargetPoller CreatePoller(TimeSpan? pollIntervalOverride = null)
            => new(
                Rooms,
                Targets,
                Settings,
                platform => platform == Provider.Platform ? Provider : null,
                (_, _) => Task.FromResult<string?>(null),
                Host,
                pollIntervalOverride ?? TimeSpan.FromSeconds(30),
                Delay);
    }

    /// <summary>窄假宿主：记录轮询器交回的起录/重置/日志决策，不启动真实录制。</summary>
    private sealed class FakePollerHost : IRecordingTargetPollerHost
    {
        private readonly object _gate = new();
        private readonly HashSet<Guid> _recording = new();
        private readonly HashSet<Guid> _suppressed = new();
        private readonly List<Guid> _started = new();
        private readonly List<Guid> _reset = new();
        private readonly List<string> _logs = new();
        private readonly List<string> _statusMessages = new();

        public IReadOnlyList<Guid> StartedRoomIds
        {
            get
            {
                lock (_gate)
                {
                    return _started.ToList();
                }
            }
        }

        public IReadOnlyList<Guid> ResetRoomIds
        {
            get
            {
                lock (_gate)
                {
                    return _reset.ToList();
                }
            }
        }

        public IReadOnlyList<string> Logs
        {
            get
            {
                lock (_gate)
                {
                    return _logs.ToList();
                }
            }
        }

        public IReadOnlyList<string> StatusMessages
        {
            get
            {
                lock (_gate)
                {
                    return _statusMessages.ToList();
                }
            }
        }

        public bool IsRecording(Guid liveRoomId)
        {
            lock (_gate)
            {
                return _recording.Contains(liveRoomId);
            }
        }

        public bool IsAutoRestartSuppressed(Guid liveRoomId)
        {
            lock (_gate)
            {
                return _suppressed.Contains(liveRoomId);
            }
        }

        public void MarkRecording(Guid liveRoomId)
        {
            lock (_gate)
            {
                _recording.Add(liveRoomId);
            }
        }

        public void Suppress(Guid liveRoomId)
        {
            lock (_gate)
            {
                _suppressed.Add(liveRoomId);
            }
        }

        public void ResetRoomPollingState(Guid liveRoomId)
        {
            lock (_gate)
            {
                _suppressed.Remove(liveRoomId);
                _reset.Add(liveRoomId);
            }
        }

        public Task StartAutoRecordingAsync(
            LiveRoom room,
            IPlatformProvider provider,
            string? cookie,
            RoomStatus status,
            CancellationToken ct)
        {
            lock (_gate)
            {
                _started.Add(room.Id);
            }

            return Task.CompletedTask;
        }

        public void Log(string message)
        {
            lock (_gate)
            {
                _logs.Add(message);
            }
        }

        public void NotifyStatus(string message)
        {
            lock (_gate)
            {
                _statusMessages.Add(message);
            }
        }

        public string FormatRoomDisplayName(LiveRoom room) => RecordingManager.FormatRoomDisplayName(room);
    }

    /// <summary>主库不可用的假仓储，用于验证读取失败时本轮轮询被跳过而不是让循环崩溃。</summary>
    private sealed class ThrowingRoomRepository : IRoomRepository
    {
        public Task<IReadOnlyList<LiveRoom>> GetAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("主库不可用");

        public Task<LiveRoom?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(PlatformType platform, string roomId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> GetNextDisplayNumberAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AddAsync(LiveRoom room, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(LiveRoom room, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
