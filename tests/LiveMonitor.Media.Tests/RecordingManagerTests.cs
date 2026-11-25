using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class RecordingManagerTests
{
    [Fact]
    public async Task StartAsync_BeforeInitialize_Throws()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        harness.SetLive();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("请先调用 InitializeAsync", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenRoomOffline_Throws()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetOffline();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("主播未开播", ex.Message);
        Assert.Empty(harness.Sessions.Snapshot);
    }

    [Fact]
    public async Task StartAsync_WithoutUsableQuality_Throws()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive(RecordingManagerHarness.Quality("blank", 1080, 60, streamUrl: " "));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("没有可用的清晰度", ex.Message);
    }

    [Fact]
    public async Task StartAsync_ChoosesHighestHeightThenFps()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive(
            RecordingManagerHarness.Quality("720p60", 720, 60),
            RecordingManagerHarness.Quality("1080p30", 1080, 30),
            RecordingManagerHarness.Quality("1080p60", 1080, 60));

        await harness.Manager.StartAsync(room);

        Assert.Equal("1080p60", harness.Recorder.SourceQuality!.Id);
        Assert.True(harness.Manager.IsRecording(room.Id));
    }

    [Fact]
    public async Task StartAsync_PersistsSessionAndNamesFiles()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom(roomId: "1001", alias: "晚风游戏室", displayNumber: 3);
        harness.SetLive();

        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;

        var session = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(RecordingStatus.Recording, session.Status);
        Assert.Equal(room.Id, session.LiveRoomId);
        Assert.Equal("#3 晚风游戏室", session.RoomDisplayName);
        Assert.Equal("测试直播间标题", session.StreamTitle);
        Assert.EndsWith(".mkv", session.VideoFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".jsonl", session.DanmakuFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(harness.OutputDirectory, session.VideoFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(harness.Recorder.OutputPath);
        Assert.StartsWith(harness.OutputDirectory, harness.Recorder.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".part001.mkv", harness.Recorder.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(session.VideoFilePath), Path.GetDirectoryName(harness.Recorder.OutputPath));
        Assert.Equal(1, harness.Recorder.StartCount);
        Assert.Single(harness.Started);
        Assert.Single(harness.ProgressItems);
    }

    [Fact]
    public async Task StartAsync_WithCustomSaveDirectory_UsesTargetDirectory()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        var customDirectory = Path.Combine(harness.RootDirectory, "定制目录");
        harness.Targets.Set(room.Id, autoRecordEnabled: false, customSaveDirectory: customDirectory);

        await harness.Manager.StartAsync(room);

        var session = await harness.Sessions.GetByIdAsync(harness.Manager.GetActiveSessionId(room.Id)!.Value);
        Assert.NotNull(session);
        Assert.StartsWith(customDirectory, session.VideoFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(customDirectory));
    }

    [Fact]
    public async Task StartAsync_WithoutTargetDirectory_UsesConfiguredDefaultDirectory()
    {
        await using var harness = new RecordingManagerHarness(useGlobalDirectoryOverride: false);
        var configuredDirectory = Path.Combine(harness.RootDirectory, "settings-dir");
        harness.Settings.Values[AppSettingKeys.RecordingDefaultDirectory] = configuredDirectory;
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();

        await harness.Manager.StartAsync(room);

        var session = await harness.Sessions.GetByIdAsync(harness.Manager.GetActiveSessionId(room.Id)!.Value);
        Assert.NotNull(session);
        Assert.StartsWith(configuredDirectory, session.VideoFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_WhileAlreadyRecording_Throws()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("正在录制中", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenRecorderFactoryThrows_MarksSessionFailed()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        harness.RecorderFactoryException = new InvalidOperationException("工厂失败（测试注入）");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("工厂失败（测试注入）", ex.Message);
        Assert.False(harness.Manager.IsRecording(room.Id));
        var session = Assert.Single(harness.Sessions.Snapshot);
        Assert.Equal(RecordingStatus.Failed, session.Status);
        Assert.Contains("创建录制器失败", session.ErrorMessage);
        Assert.False(File.Exists(session.VideoFilePath));
    }

    [Fact]
    public async Task StartAsync_WhenRecorderStartThrows_CleansUpFilesAndMarksFailed()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        harness.NextRecorderStartException = new InvalidOperationException("启动失败（测试注入）");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.StartAsync(room));

        Assert.Contains("启动失败（测试注入）", ex.Message);
        var recorder = harness.Recorder;
        Assert.True(recorder.DisposeCount >= 1);
        var session = Assert.Single(harness.Sessions.Snapshot);
        Assert.Equal(RecordingStatus.Failed, session.Status);
        Assert.Contains("启动录制失败", session.ErrorMessage);
        Assert.False(File.Exists(session.VideoFilePath));
        Assert.False(File.Exists(session.DanmakuFilePath));
    }

    [Fact]
    public async Task StopAsync_CompletesSession_AndRaisesEvents()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var session = await harness.Sessions.GetByIdAsync(harness.Manager.GetActiveSessionId(room.Id)!.Value);
        Assert.NotNull(session);
        var recorder = harness.Recorder;
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        recorder.RaiseProgress(TimeSpan.FromSeconds(5), 4096, 1600);

        await harness.Manager.StopAsync(room.Id);

        Assert.False(harness.Manager.IsRecording(room.Id));
        var stored = await harness.Sessions.GetByIdAsync(session.Id);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);
        Assert.NotNull(stored.EndedAt);
        Assert.True(stored.DurationSeconds >= 4.5);
        Assert.True(recorder.StopCount >= 1);
        Assert.Single(harness.Completed);
        Assert.Contains(harness.StatusMessages, message => message.Contains("已停止录制"));
        Assert.Empty(harness.Manager.GetActiveRecordings());
    }

    [Fact]
    public async Task StopAsync_WithoutActiveRecording_IsNoOp()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();

        await harness.Manager.StopAsync(room.Id);

        Assert.False(harness.Manager.IsRecording(room.Id));
        Assert.Empty(harness.Sessions.Snapshot);
    }

    [Fact]
    public async Task RecorderExit_WhileRoomLive_StartsNextPart_ThenMergesOnStop()
    {
        await using var harness = new RecordingManagerHarness(delay: (_, _) => Task.CompletedTask);
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        var firstPart = harness.Recorder;
        var firstPartPath = firstPart.OutputPath;
        Assert.NotNull(firstPartPath);
        Assert.EndsWith(".part001.mkv", firstPartPath, StringComparison.OrdinalIgnoreCase);
        firstPart.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        firstPart.RaiseProgress(TimeSpan.FromSeconds(2), 2048, 800);

        // 虎牙等 CDN 单连接会突然结束：录制器退出后应自动开下一段，而不是直接失败。
        firstPart.RaiseExit(1, "stream ends prematurely");

        await RecordingManagerHarness.WaitUntilAsync(
            () => harness.Recorders.Count >= 2,
            timeoutMilliseconds: 8000,
            message: "等待自动续段超时。");
        var secondPart = harness.Recorder;
        Assert.NotSame(firstPart, secondPart);
        Assert.True(harness.Manager.IsRecording(room.Id));
        Assert.Contains(harness.StatusMessages, message => message.Contains("第 2 段"));
        Assert.EndsWith(".part002.mkv", secondPart.OutputPath!, StringComparison.OrdinalIgnoreCase);

        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        secondPart.RaiseProgress(TimeSpan.FromSeconds(3), 4096, 800);
        await harness.Manager.StopAsync(room.Id);

        Assert.Single(harness.SegmentMerger.Batches);
        Assert.Equal(2, harness.SegmentMerger.Batches[0].Count);
        Assert.Contains(firstPartPath, harness.SegmentMerger.Batches[0].Select(segment => segment.Path));
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);
        Assert.Null(stored.ErrorMessage);
        Assert.True(stored.DurationSeconds >= 4.5);
        Assert.True(File.Exists(stored.VideoFilePath));
        Assert.False(File.Exists(firstPartPath));
    }

    [Fact]
    public async Task RecorderExit_WhileRoomLive_WithOverlappingTimeline_PassesTimelineToMerger()
    {
        await using var harness = new RecordingManagerHarness(delay: (_, _) => Task.CompletedTask);
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var firstPart = harness.Recorder;
        // 模拟虎牙 CDN：每段从缓存关键帧重新下发，起点比上一段末尾早约 1.1 秒。
        firstPart.SourceTimelineStartSeconds = 29066.649;
        firstPart.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        firstPart.RaiseProgress(TimeSpan.FromSeconds(2), 2048, 800);
        firstPart.RaiseExit(1, "stream ends prematurely");

        await RecordingManagerHarness.WaitUntilAsync(
            () => harness.Recorders.Count >= 2,
            timeoutMilliseconds: 8000,
            message: "等待自动续段超时。");
        var secondPart = harness.Recorder;
        secondPart.SourceTimelineStartSeconds = 29067.549;
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        secondPart.RaiseProgress(TimeSpan.FromSeconds(3), 4096, 800);
        await harness.Manager.StopAsync(room.Id);

        // 合并器应拿到每段的内容时长与输入时间轴起点，用于裁掉 CDN 重发的重叠内容。
        Assert.Single(harness.SegmentMerger.Batches);
        var merged = harness.SegmentMerger.Batches[0];
        Assert.Equal(2, merged.Count);
        Assert.Equal(29066.649, merged[0].SourceTimelineStartSeconds);
        Assert.Equal(2.0, merged[0].Duration.TotalSeconds, 3);
        Assert.Equal(29067.549, merged[1].SourceTimelineStartSeconds);
        Assert.Equal(3.0, merged[1].Duration.TotalSeconds, 3);
    }

    [Fact]
    public async Task RecorderExit_WhileRoomLive_WithRestartFailures_MarksSessionFailed()
    {
        await using var harness = new RecordingManagerHarness(delay: (_, _) => Task.CompletedTask);
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        harness.RecorderFactoryException = new InvalidOperationException("part factory boom");

        harness.Recorder.RaiseExit(1, "stream ends prematurely");

        await RecordingManagerHarness.WaitUntilAsync(
            () => !harness.Manager.IsRecording(room.Id),
            timeoutMilliseconds: 8000,
            message: "等待续段失败收尾超时。");
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Failed, stored.Status);
        Assert.Contains("录制启动失败", stored.ErrorMessage);
        Assert.Contains("part factory boom", stored.ErrorMessage);
        Assert.Single(harness.Recorders);
        Assert.Contains(harness.Logs, log => log.Contains("停止自动续段"));
    }

    [Fact]
    public async Task StopAsync_WithSinglePart_MergesThroughMerger()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        var recorder = harness.Recorder;
        recorder.CreateOutputFile = true;
        recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        recorder.RaiseProgress(TimeSpan.FromSeconds(4), 4096, 1600);

        await harness.Manager.StopAsync(room.Id);

        Assert.Single(harness.SegmentMerger.Batches);
        Assert.Single(harness.SegmentMerger.Batches[0]);
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);
        Assert.True(File.Exists(stored.VideoFilePath));
    }

    [Fact]
    public async Task StopAsync_WhenMergeFails_KeepsCompletedButRecordsMessage()
    {
        await using var harness = new RecordingManagerHarness();
        harness.SegmentMerger.Result = new SegmentMergeResult(false, 8192, "concat boom", TimeSpan.Zero);
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        harness.Recorder.RaiseAnchor(harness.Clock.Now);
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        harness.Recorder.RaiseProgress(TimeSpan.FromSeconds(3), 2048, 800);

        await harness.Manager.StopAsync(room.Id);

        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);
        Assert.Contains("录制分片合并失败", stored.ErrorMessage);
        Assert.Contains("concat boom", stored.ErrorMessage);
    }

    [Fact]
    public async Task RecorderExit_AfterRoomGoesOffline_MarksSessionCompleted()
    {
        await using var harness = new RecordingManagerHarness(delay: (_, _) => Task.CompletedTask);
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        harness.SetOffline();

        harness.Recorder.RaiseExit(0, null);

        await RecordingManagerHarness.WaitUntilAsync(() => !harness.Manager.IsRecording(room.Id));
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);
        Assert.Null(stored.ErrorMessage);
        Assert.Single(harness.SegmentMerger.Batches);
    }

    [Fact]
    public async Task StallDetected_MarksSessionFailed()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        harness.Recorder.RaiseAnchor(harness.Clock.Now);
        harness.Recorder.RaiseProgress(TimeSpan.FromSeconds(1), 1024, 800);

        harness.Clock.Advance(TimeSpan.FromSeconds(31));

        await RecordingManagerHarness.WaitUntilAsync(
            () => !harness.Manager.IsRecording(room.Id),
            timeoutMilliseconds: 8000,
            message: "等待卡死判定收尾超时。");
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Failed, stored.Status);
        Assert.Contains("卡死", stored.ErrorMessage);
        Assert.Contains(harness.Logs, log => log.Contains("卡死"));
    }

    [Fact]
    public async Task Initialize_RecoversUnfinishedSessions()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        var videoDirectory = Path.Combine(harness.RootDirectory, "recovery");
        Directory.CreateDirectory(videoDirectory);
        var videoPath = Path.Combine(videoDirectory, "recovered.mkv");
        await File.WriteAllBytesAsync(videoPath, new byte[4096]);
        var session = new RecordingSession
        {
            Id = Guid.NewGuid(),
            LiveRoomId = room.Id,
            Platform = PlatformType.Bilibili,
            RoomId = room.RoomId,
            RoomDisplayName = "#1 测试主播",
            Status = RecordingStatus.Recording,
            VideoFilePath = videoPath,
            DanmakuFilePath = videoPath + ".jsonl",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        harness.Sessions.Seed(session);

        await harness.InitializeAsync();

        var recovered = await harness.Sessions.GetByIdAsync(session.Id);
        Assert.NotNull(recovered);
        Assert.Equal(RecordingStatus.Failed, recovered.Status);
        Assert.NotNull(recovered.EndedAt);
        Assert.Equal(4096, recovered.FileSizeBytes);
        Assert.Contains("程序异常退出", recovered.ErrorMessage);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("30", 30)]
    [InlineData("5", 10)]
    [InlineData("9999", 600)]
    [InlineData("abc", 60)]
    public async Task Initialize_ReadsAndClampsPollInterval(string? setting, int expectedSeconds)
    {
        await using var harness = new RecordingManagerHarness(readPollIntervalFromSettings: true);
        if (setting is not null)
        {
            harness.Settings.Values[AppSettingKeys.RecordingPollIntervalSeconds] = setting;
        }

        await harness.InitializeAsync();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), harness.Manager.PollInterval);
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await using var harness = new RecordingManagerHarness();

        await harness.InitializeAsync();
        await harness.InitializeAsync();

        // 覆盖值同样受 10~600 秒钳制：60 分钟会被收敛为 10 分钟。
        Assert.Equal(TimeSpan.FromMinutes(10), harness.Manager.PollInterval);
    }

    [Fact]
    public async Task PollOnce_AutoRecordLiveRoom_StartsRecording()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        harness.Targets.Set(room.Id, autoRecordEnabled: true);
        harness.SetLive();
        await harness.InitializeAsync();

        await harness.Manager.PollOnceAsync();

        Assert.True(harness.Manager.IsRecording(room.Id));
        Assert.Single(harness.Recorders);
    }

    [Fact]
    public async Task PollOnce_OfflineRoom_DoesNotStartRecording()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        harness.Targets.Set(room.Id, autoRecordEnabled: true);
        harness.SetOffline();
        await harness.InitializeAsync();

        await harness.Manager.PollOnceAsync();

        Assert.False(harness.Manager.IsRecording(room.Id));
        Assert.Empty(harness.Recorders);
    }

    [Fact]
    public async Task PollOnce_SkipsRoomsAlreadyRecording()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        harness.Targets.Set(room.Id, autoRecordEnabled: true);
        harness.SetLive();
        await harness.InitializeAsync();
        await harness.Manager.PollOnceAsync();

        await harness.Manager.PollOnceAsync();

        Assert.Single(harness.Recorders);
    }

    [Fact]
    public async Task ManualStop_WhileAutoRecordEnabled_SuppressesRestartUntilRoomGoesOffline()
    {
        await using var harness = new RecordingManagerHarness();
        var room = harness.AddRoom();
        harness.Targets.Set(room.Id, autoRecordEnabled: true);
        harness.SetLive();
        await harness.InitializeAsync();
        await harness.Manager.PollOnceAsync();
        Assert.True(harness.Manager.IsRecording(room.Id));

        await harness.Manager.StopAsync(room.Id);
        await harness.Manager.PollOnceAsync();
        Assert.False(harness.Manager.IsRecording(room.Id));

        harness.SetOffline();
        await harness.Manager.PollOnceAsync();
        harness.SetLive();
        await harness.Manager.PollOnceAsync();

        Assert.True(harness.Manager.IsRecording(room.Id));
        Assert.Equal(2, harness.Recorders.Count);
    }

    [Fact]
    public async Task DeleteSession_RemovesFilesAndRecord()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        await harness.Manager.StopAsync(room.Id);
        var session = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(session);
        var videoDirectory = Path.GetDirectoryName(session.VideoFilePath);
        if (!string.IsNullOrEmpty(videoDirectory))
        {
            Directory.CreateDirectory(videoDirectory);
        }

        await File.WriteAllBytesAsync(session.VideoFilePath, new byte[1024]);
        await File.WriteAllTextAsync(session.DanmakuFilePath, "{\"type\":\"meta\"}");

        await harness.Manager.DeleteSessionAsync(sessionId);

        Assert.False(File.Exists(session.VideoFilePath));
        Assert.False(File.Exists(session.DanmakuFilePath));
        Assert.Null(await harness.Sessions.GetByIdAsync(sessionId));
    }

    [Fact]
    public async Task DeleteSession_WhileRecording_Throws()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.DeleteSessionAsync(sessionId));

        Assert.Contains("仍在进行中", ex.Message);
    }

    [Fact]
    public async Task SetAutoRecordAsync_PersistsAndClearsCustomDirectory()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();

        await harness.Manager.SetAutoRecordAsync(room.Id, true, @"D:\录像\甲");
        var target = await harness.Targets.GetAsync(room.Id);
        Assert.NotNull(target);
        Assert.True(target.AutoRecordEnabled);
        Assert.Equal(@"D:\录像\甲", target.CustomSaveDirectory);

        await harness.Manager.SetAutoRecordAsync(room.Id, false, "  ");
        target = await harness.Targets.GetAsync(room.Id);
        Assert.NotNull(target);
        Assert.False(target.AutoRecordEnabled);
        Assert.Null(target.CustomSaveDirectory);
    }

    [Fact]
    public async Task GetActiveRecordings_ExposesProgress()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        harness.Recorder.RaiseAnchor(harness.Clock.Now);
        harness.Recorder.RaiseProgress(TimeSpan.FromSeconds(3), 8192, 2500);

        var active = Assert.Single(harness.Manager.GetActiveRecordings());
        Assert.Equal(room.Id, active.Room.Id);
        Assert.Equal(3, active.Progress.Duration.TotalSeconds, precision: 3);
        Assert.Equal(8192, active.Progress.FileSizeBytes);
        var progress = harness.Manager.GetProgress(room.Id);
        Assert.NotNull(progress);
        Assert.NotNull(harness.Manager.GetActiveSessionId(room.Id));
    }

    [Fact]
    public async Task DisposeAsync_StopsActiveRecording_AndIsIdempotent()
    {
        var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;

        await harness.Manager.DisposeAsync();
        await harness.Manager.DisposeAsync();

        Assert.False(harness.Manager.IsRecording(room.Id));
        var stored = await harness.Sessions.GetByIdAsync(sessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Completed, stored.Status);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task GetLiveRecordings_ExposesPartPathAndRealtimeProgress()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();
        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;
        harness.Recorder.RaiseAnchor(harness.Clock.Now);
        harness.Recorder.RaiseProgress(TimeSpan.FromSeconds(3), 8192, 2500);

        var live = Assert.Single(harness.Manager.GetLiveRecordings());
        Assert.Equal(sessionId, live.SessionId);
        Assert.Equal(room.Id, live.LiveRoomId);
        Assert.Equal(1, live.RoomDisplayNumber);
        Assert.Equal("#1 测试主播", live.RoomDisplayName);
        Assert.Equal(1, live.PartNumber);
        Assert.EndsWith(".part001.mkv", live.PartFilePath);
        Assert.Equal(3, live.RecordedDuration.TotalSeconds, precision: 3);
        Assert.Equal(8192, live.FileSizeBytes);
        Assert.Equal(TimeSpan.Zero, live.CompletedDuration);
    }

    [Fact]
    public async Task TryGetLiveRecording_ReturnsFalseForUnknownOrInactiveSession()
    {
        await using var harness = new RecordingManagerHarness();
        await harness.InitializeAsync();
        var room = harness.AddRoom();
        harness.SetLive();

        Assert.False(harness.Manager.TryGetLiveRecording(Guid.NewGuid(), out _));

        await harness.Manager.StartAsync(room);
        var sessionId = harness.Manager.GetActiveSessionId(room.Id)!.Value;

        Assert.True(harness.Manager.TryGetLiveRecording(sessionId, out var info));
        Assert.Equal(sessionId, info.SessionId);
        Assert.EndsWith(".part001.mkv", info.PartFilePath);

        await harness.Manager.StopAsync(room.Id);

        Assert.False(harness.Manager.TryGetLiveRecording(sessionId, out _));
        Assert.Empty(harness.Manager.GetLiveRecordings());
    }

    [Fact]
    public void FormatRoomDisplayName_UsesAliasAndDisplayNumber()
    {
        var room = new LiveRoom
        {
            Platform = PlatformType.Bilibili,
            RoomId = "1001",
            Alias = string.Empty,
            DisplayNumber = 0,
        };

        Assert.Equal("Bilibili_1001", RecordingManager.FormatRoomDisplayName(room));

        room.Alias = "晚风";
        room.DisplayNumber = 3;

        Assert.Equal("#3 晚风", RecordingManager.FormatRoomDisplayName(room));
    }

    [Fact]
    public void DefaultDirectory_IsUnderUserVideos()
    {
        Assert.EndsWith(
            Path.Combine("Videos", "LimoClip"),
            RecordingManager.DefaultDirectory,
            StringComparison.OrdinalIgnoreCase);
    }
}
