using System.Collections;
using System.Reflection;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Media.Recording;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// RecordingCenterViewModel 基线测试：刷新目标/历史、删除确认流程、剪辑事件、直播状态排序与释放语义。
/// 测试直接构造录制中心（不经过 MainWindow），并共用 <see cref="ViewModelTestHarness"/> 的临时数据库。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class RecordingCenterViewModelTests
{
    private const string TestClassName = nameof(RecordingCenterViewModelTests);

    [Fact]
    public Task RefreshAsync_BuildsTargetsOrderedByDisplayNumberAndAppliesTargets() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var second = await ViewModelTestHarness.AddRoomAsync(context, 2, "第二间", groupTag: "分组乙");
            var first = await ViewModelTestHarness.AddRoomAsync(context, 1, "第一间", groupTag: "分组甲");
            await context.Services.RecordingTargets.UpsertAsync(new RecordingTarget
            {
                LiveRoomId = first.Id,
                AutoRecordEnabled = true,
                CustomSaveDirectory = context.TempDirectory,
                UpdatedAt = DateTime.UtcNow
            });

            await center.RefreshAsync();

            Assert.Equal(new[] { 1, 2 }, center.Targets.Select(item => item.Room.DisplayNumber));
            Assert.Equal(new[] { first.Id, second.Id }, center.Targets.Select(item => item.RoomId));
            Assert.Equal("#1 第一间", center.Targets[0].DisplayTitle);
            Assert.True(center.Targets[0].AutoRecordEnabled);
            Assert.Equal(context.TempDirectory, center.Targets[0].CustomSaveDirectory);
            Assert.True(center.Targets[0].HasCustomDirectory);
            Assert.False(center.Targets[1].AutoRecordEnabled);
            Assert.False(center.Targets[1].HasCustomDirectory);
            Assert.False(center.IsTargetsEmpty);
            Assert.True(center.IsHistoryEmpty);
            Assert.False(center.IsBusy);
            Assert.Equal("共 2 个直播间 · 正在录制 0 路", center.TargetSummary);
            Assert.Equal("最近 0 条录像", center.HistorySummary);
            Assert.Equal("已刷新：2 个直播间 · 0 条历史录像", center.StatusText);
        });

    [Fact]
    public Task RefreshAsync_UsesRecordingManagerDefaultDirectoryWhenSettingMissing() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            await center.RefreshAsync();
            Assert.Equal(RecordingManager.DefaultDirectory, center.GlobalDirectory);

            var customDirectory = Path.Combine(context.TempDirectory, "records");
            Directory.CreateDirectory(customDirectory);
            await context.Services.Settings.SetAsync(AppSettingKeys.RecordingDefaultDirectory, customDirectory);

            await center.RefreshAsync();

            Assert.Equal(customDirectory, center.GlobalDirectory);
        });

    [Fact]
    public Task RefreshAsync_SortsLiveTargetsBeforeOfflineTargets() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var offline = await ViewModelTestHarness.AddRoomAsync(context, 1, "未开播");
            var live = await ViewModelTestHarness.AddRoomAsync(context, 2, "直播中");
            var unknown = await ViewModelTestHarness.AddRoomAsync(context, 3, "状态未知");
            var probeCount = 0;
            center.TryGetLiveStatus = roomId =>
            {
                probeCount++;
                if (roomId == live.Id)
                {
                    return true;
                }

                return roomId == offline.Id ? false : null;
            };

            await center.RefreshAsync();

            Assert.Equal(3, probeCount);
            Assert.Equal(new[] { live.Id, offline.Id, unknown.Id }, center.Targets.Select(item => item.RoomId));
            Assert.True(center.Targets[0].IsLive);
            Assert.False(center.Targets[1].IsLive);
            Assert.False(center.Targets[2].IsLive);
            Assert.False(center.Targets[1].ShowLiveBadge);
        });

    [Fact]
    public Task RefreshHistoryAsync_LoadsRecentSessionsNewestFirst() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "历史房间");
            Assert.True(center.IsHistoryEmpty);

            var older = await AddSessionAsync(context, room, DateTime.UtcNow.AddHours(-2), roomDisplayName: "历史房间");
            var newer = await AddSessionAsync(context, room, DateTime.UtcNow.AddHours(-1), roomDisplayName: "历史房间");

            await center.RefreshHistoryAsync();

            Assert.False(center.IsHistoryEmpty);
            Assert.Equal(new[] { newer.Id, older.Id }, center.History.Select(item => item.SessionId));
            Assert.Equal("历史房间", center.History[0].RoomTitle);
            Assert.Equal("最近 2 条录像", center.HistorySummary);
        });

    [Fact]
    public Task DeleteCommand_WithoutConfirmationHandler_KeepsRecord() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var session = await AddSessionAsync(context, room: null, DateTime.UtcNow.AddMinutes(-5));
            await center.RefreshHistoryAsync();
            var item = center.History[0];

            await item.DeleteCommand.ExecuteAsync(null);

            Assert.Equal("当前界面未接入删除确认弹窗，已取消删除。", center.StatusText);
            Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
            Assert.Single(center.History);
        });

    [Fact]
    public Task DeleteCommand_WhenCancelled_KeepsRecord() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var session = await AddSessionAsync(context, room: null, DateTime.UtcNow.AddMinutes(-5));
            await center.RefreshHistoryAsync();
            var confirmCount = 0;
            center.ConfirmDeleteAsync = _ =>
            {
                confirmCount++;
                return Task.FromResult(false);
            };

            await center.History[0].DeleteCommand.ExecuteAsync(null);

            Assert.Equal(1, confirmCount);
            Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
            Assert.Single(center.History);
        });

    [Fact]
    public Task DeleteCommand_WhenConfirmed_DeletesRecordAndRefreshes() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "deleted-clip.mp4");
            await File.WriteAllTextAsync(videoPath, "fake video");
            var session = await AddSessionAsync(context, room: null, DateTime.UtcNow.AddMinutes(-5), videoFilePath: videoPath);
            await center.RefreshHistoryAsync();
            var item = center.History[0];
            center.ConfirmDeleteAsync = _ => Task.FromResult(true);

            await item.DeleteCommand.ExecuteAsync(null);

            Assert.Null(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
            Assert.False(File.Exists(videoPath));
            Assert.True(center.IsHistoryEmpty);
            Assert.Equal($"已删除录制记录：{item.RoomTitle} · {item.StartedAtText}", center.StatusText);
        });

    [Fact]
    public Task DeleteCommand_WhenSessionActive_RefusesDeletion() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "录制中房间");
            var session = await AddSessionAsync(context, room, DateTime.UtcNow, RecordingStatus.Recording);
            await center.RefreshHistoryAsync();
            var item = center.History[0];
            Assert.True(item.IsActive);
            Assert.False(item.CanDelete);

            await item.DeleteCommand.ExecuteAsync(null);

            Assert.Equal("该录制仍在进行中，请先停止录制再删除记录。", center.StatusText);
            Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));

            var opened = new List<RecordingSession>();
            center.OpenClipStudioAsync = openedSession =>
            {
                opened.Add(openedSession);
                return Task.CompletedTask;
            };
            await item.ClipCommand.ExecuteAsync(null);
            Assert.Equal("该录制仍在进行中，请先停止录制再进入剪辑台。", center.StatusText);
            Assert.Empty(opened);
        });

    [Fact]
    public Task ClipCommand_WhenFileExists_RequestsOpenOnce() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "clip.mp4");
            await File.WriteAllTextAsync(videoPath, "fake video");
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "可剪辑房间");
            var session = await AddSessionAsync(context, room, DateTime.UtcNow.AddMinutes(-1), videoFilePath: videoPath);
            await center.RefreshHistoryAsync();
            var opened = new List<RecordingSession>();
            center.OpenClipStudioAsync = openedSession =>
            {
                opened.Add(openedSession);
                return Task.CompletedTask;
            };

            await center.History[0].ClipCommand.ExecuteAsync(null);

            var captured = Assert.Single(opened);
            Assert.Equal(session.Id, captured.Id);
            Assert.Equal(videoPath, captured.VideoFilePath);
        });

    [Fact]
    public Task ClipCommand_WhenFileMissing_ReportsStatus() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var missingPath = Path.Combine(context.TempDirectory, "missing.mp4");
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "文件缺失房间");
            await AddSessionAsync(context, room, DateTime.UtcNow.AddMinutes(-1), videoFilePath: missingPath);
            await center.RefreshHistoryAsync();
            var opened = new List<RecordingSession>();
            center.OpenClipStudioAsync = openedSession =>
            {
                opened.Add(openedSession);
                return Task.CompletedTask;
            };

            await center.History[0].ClipCommand.ExecuteAsync(null);

            Assert.Equal($"录像文件不存在，无法剪辑：{missingPath}", center.StatusText);
            Assert.Empty(opened);
        });

    [Fact]
    public Task ClipCommand_WithoutHandler_ReportsNotConnected() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "connected.mp4");
            await File.WriteAllTextAsync(videoPath, "fake video");
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "未接入房间");
            await AddSessionAsync(context, room, DateTime.UtcNow.AddMinutes(-1), videoFilePath: videoPath);
            await center.RefreshHistoryAsync();

            await center.History[0].ClipCommand.ExecuteAsync(null);

            Assert.Equal("当前界面未接入剪辑台。", center.StatusText);
        });

    [Fact]
    public Task LiveClipCommand_WhenNoActiveRecording_ReportsAndRefreshes() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "直播房间");
            await center.RefreshAsync();
            var opened = new List<RecordingSession>();
            center.OpenClipStudioAsync = openedSession =>
            {
                opened.Add(openedSession);
                return Task.CompletedTask;
            };

            await center.Targets[0].LiveClipCommand.ExecuteAsync(null);

            Assert.Empty(opened);
            Assert.Equal("已刷新：1 个直播间 · 0 条历史录像", center.StatusText);
        });

    [Fact]
    public Task LiveClipCommand_WhenActiveRecording_OpensClipStudioOnce() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "边录边剪房间");
            var session = await AddSessionAsync(
                context,
                room,
                DateTime.UtcNow,
                RecordingStatus.Recording,
                roomDisplayName: "边录边剪房间");
            var active = CreateActiveRecording(room, session);
            var activeEntries = GetActiveRecordingEntries(context);
            activeEntries[room.Id] = active;
            try
            {
                await center.RefreshAsync();
                var opened = new List<RecordingSession>();
                center.OpenClipStudioAsync = openedSession =>
                {
                    opened.Add(openedSession);
                    return Task.CompletedTask;
                };

                var target = center.Targets.Single(item => item.RoomId == room.Id);
                await target.LiveClipCommand.ExecuteAsync(null);

                var captured = Assert.Single(opened);
                Assert.Equal(session.Id, captured.Id);
            }
            finally
            {
                activeEntries.Remove(room.Id);
            }
        });

    [Fact]
    public Task EditGlobalDirectoryCommand_PersistsSelectionAndUpdatesTargets() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            await center.RefreshAsync();
            var defaultDirectory = center.GlobalDirectory;

            await center.EditGlobalDirectoryCommand.ExecuteAsync(null);
            Assert.Equal("当前界面未接入目录选择器，无法修改全局目录。", center.StatusText);

            var customDirectory = Path.Combine(context.TempDirectory, "recordings-custom");
            center.PickDirectoryAsync = () => Task.FromResult<string?>(customDirectory);

            await center.EditGlobalDirectoryCommand.ExecuteAsync(null);

            Assert.True(Directory.Exists(customDirectory));
            Assert.Equal(customDirectory, center.GlobalDirectory);
            Assert.Equal(customDirectory, await context.Services.Settings.GetAsync(AppSettingKeys.RecordingDefaultDirectory));
            Assert.Contains("全局录像目录已更新：", center.StatusText);
            Assert.NotEqual(defaultDirectory, center.GlobalDirectory);

            await center.RefreshAsync();
            Assert.Equal(customDirectory, center.GlobalDirectory);
        });

    [Fact]
    public Task InitializeAndDispose_SubscribesOnceDetachesAndIsIdempotent() =>
        ViewModelTestHarness.RunRecordingCenterAsync(TestClassName, async (context, center) =>
        {
            var recordings = context.Services.Recordings;
            var eventNames = new[] { "ProgressUpdated", "SessionStarted", "SessionCompleted", "StatusMessage" };
            var baseline = eventNames.ToDictionary(name => name, name => CountEventSubscribers(recordings, name));

            await center.InitializeAsync();

            foreach (var name in eventNames)
            {
                Assert.Equal(baseline[name] + 1, CountEventSubscribers(recordings, name));
            }

            center.Dispose();

            foreach (var name in eventNames)
            {
                Assert.Equal(baseline[name], CountEventSubscribers(recordings, name));
            }

            center.Dispose();
            await center.RefreshAsync();
            Assert.Empty(center.Targets);
            Assert.Empty(center.History);
        });

    private static async Task<RecordingSession> AddSessionAsync(
        ViewModelTestHarness.ViewModelTestContext context,
        LiveRoom? room,
        DateTime startedAt,
        RecordingStatus status = RecordingStatus.Completed,
        string? videoFilePath = null,
        string danmakuFilePath = "",
        string roomDisplayName = "测试房间")
    {
        var session = new RecordingSession
        {
            LiveRoomId = room?.Id,
            Platform = room?.Platform ?? PlatformType.CustomStream,
            RoomId = room?.RoomId ?? "cs_test",
            RoomDisplayName = roomDisplayName,
            Status = status,
            StartedAt = startedAt,
            VideoFilePath = videoFilePath ?? Path.Combine(context.TempDirectory, $"session-{Guid.NewGuid():N}.mp4"),
            DanmakuFilePath = danmakuFilePath
        };
        await context.Services.RecordingSessions.AddAsync(session);
        return session;
    }

    private static object CreateActiveRecording(LiveRoom room, RecordingSession session)
    {
        var activeType = typeof(RecordingManager).GetNestedType("ActiveRecording", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecordingManager.ActiveRecording 私有类型不存在，测试需要同步更新。");
        var constructor = activeType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        return constructor.Invoke(new object?[] { room, session, null, DateTimeOffset.UtcNow });
    }

    private static IDictionary GetActiveRecordingEntries(ViewModelTestHarness.ViewModelTestContext context)
    {
        var field = typeof(RecordingManager).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecordingManager._active 私有字段不存在，测试需要同步更新。");
        return (IDictionary)field.GetValue(context.Services.Recordings)!;
    }

    private static int CountEventSubscribers(object source, string eventName)
    {
        var field = source.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
