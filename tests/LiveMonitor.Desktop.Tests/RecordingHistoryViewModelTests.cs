using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 录制中心「历史录像」子视图模型基线测试：列表刷新（200 条上限/版本防串写）、
/// 资源管理器定位与打开、删除确认、进入剪辑台与边录边剪退化路径。
/// 资源管理器通过构造接缝注入，测试不会真的拉起 explorer.exe。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class RecordingHistoryViewModelTests
{
    private const string TestClassName = nameof(RecordingHistoryViewModelTests);

    private sealed class FakeHost : IRecordingHistoryHost
    {
        public FakeHost(RecordingManager recordings) => Recordings = recordings;

        public RecordingManager Recordings { get; }

        public Func<RecordingHistoryItemViewModel, Task<bool>>? ConfirmDeleteAsync { get; set; }

        public Func<RecordingSession, Task>? OpenClipStudioAsync { get; set; }

        public bool IsDisposed { get; set; }

        public List<string> Statuses { get; } = new();

        public int RefreshCenterCount { get; private set; }

        public void ReportStatus(string message) => Statuses.Add(message);

        public Task RefreshCenterAsync(CancellationToken ct = default)
        {
            RefreshCenterCount++;
            return Task.CompletedTask;
        }
    }

    private static RecordingSession CreateSession(
        string roomName,
        DateTime startedAt,
        string? videoPath = null,
        RecordingStatus status = RecordingStatus.Completed) => new()
    {
        RoomDisplayName = roomName,
        Platform = PlatformType.CustomStream,
        RoomId = $"cs_test_{Guid.NewGuid():N}",
        Status = status,
        StartedAt = startedAt,
        // RecordingSessionRepository.AddAsync 要求视频路径非空，占位路径即可（文件是否存在由具体用例控制）。
        VideoFilePath = videoPath ?? Path.Combine(Path.GetTempPath(), $"limoclip-history-{Guid.NewGuid():N}.mkv"),
        DanmakuFilePath = string.Empty,
    };

    private static RecordingHistoryViewModel CreateViewModel(
        AppServices services,
        FakeHost host,
        TryLaunch? openDirectory = null,
        TryLaunch? selectFile = null) =>
        new(services.RecordingSessions, host, openDirectory, selectFile);

    [Fact]
    public Task RefreshAsync_LoadsRecentSessionsNewestFirst() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var older = CreateSession("较早房间", DateTime.UtcNow.AddMinutes(-10));
            var newer = CreateSession("较新房间", DateTime.UtcNow.AddMinutes(-1));
            await context.Services.RecordingSessions.AddAsync(older);
            await context.Services.RecordingSessions.AddAsync(newer);

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();

                Assert.False(history.IsEmpty);
                Assert.Equal(new[] { newer.Id, older.Id }, history.Items.Select(item => item.SessionId));
                Assert.Equal("较新房间", history.Items[0].RoomTitle);
                Assert.Equal("最近 2 条录像", history.Summary);
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RefreshAsync_Empty_ReportsEmptyState() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();

                Assert.True(history.IsEmpty);
                Assert.Empty(history.Items);
                Assert.Equal("最近 0 条录像", history.Summary);
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task OpenFolderAsync_ExistingVideo_SelectsFileAndReports() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "record.mkv");
            await File.WriteAllBytesAsync(videoPath, new byte[] { 1, 2, 3 });
            await context.Services.RecordingSessions.AddAsync(CreateSession("定位房间", DateTime.UtcNow, videoPath));

            string? selected = null;
            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(
                context.Services,
                host,
                selectFile: (string target, out string? error) =>
                {
                    selected = target;
                    error = null;
                    return true;
                });
            try
            {
                await history.RefreshAsync();
                await history.Items[0].OpenFolderCommand.ExecuteAsync(null);

                Assert.Equal(videoPath, selected);
                Assert.Contains(host.Statuses, status => status.Contains("已在资源管理器中定位：record.mkv", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task OpenFolderAsync_MissingVideo_OpensParentDirectory() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "missing.mkv");
            await context.Services.RecordingSessions.AddAsync(CreateSession("缺失房间", DateTime.UtcNow, videoPath));

            string? opened = null;
            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(
                context.Services,
                host,
                openDirectory: (string target, out string? error) =>
                {
                    opened = target;
                    error = null;
                    return true;
                });
            try
            {
                await history.RefreshAsync();
                await history.Items[0].OpenFolderCommand.ExecuteAsync(null);

                Assert.Equal(context.TempDirectory, opened);
                Assert.Contains(host.Statuses, status => status.Contains("录像文件不存在，已打开所在目录", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task OpenFolderAsync_NoFileNoDirectory_ReportsNotFound() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var missingDirectory = Path.Combine(context.TempDirectory, "deleted", "gone.mkv");
            await context.Services.RecordingSessions.AddAsync(CreateSession("已删除房间", DateTime.UtcNow, missingDirectory));

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].OpenFolderCommand.ExecuteAsync(null);

                Assert.Contains(host.Statuses, status => status.Contains("找不到录像文件或所在目录", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestDeleteAsync_ActiveSession_Rejected() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var session = CreateSession("录制中房间", DateTime.UtcNow, status: RecordingStatus.Recording);
            await context.Services.RecordingSessions.AddAsync(session);

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].DeleteCommand.ExecuteAsync(null);

                Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
                Assert.Contains(host.Statuses, status => status.Contains("该录制仍在进行中", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestDeleteAsync_ConfirmCanceled_KeepsRecord() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var session = CreateSession("取消删除房间", DateTime.UtcNow);
            await context.Services.RecordingSessions.AddAsync(session);

            var host = new FakeHost(context.Services.Recordings)
            {
                ConfirmDeleteAsync = _ => Task.FromResult(false),
            };
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].DeleteCommand.ExecuteAsync(null);

                Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
                Assert.Single(history.Items);
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestDeleteAsync_NoConfirmHandler_ReportsUnavailable() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var session = CreateSession("无确认房间", DateTime.UtcNow);
            await context.Services.RecordingSessions.AddAsync(session);

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].DeleteCommand.ExecuteAsync(null);

                Assert.Contains(host.Statuses, status => status.Contains("未接入删除确认弹窗", StringComparison.Ordinal));
                Assert.Single(history.Items);
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestDeleteAsync_Confirmed_DeletesSessionAndRefreshes() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "delete-me.mkv");
            await File.WriteAllBytesAsync(videoPath, new byte[] { 9 });
            var session = CreateSession("确认删除房间", DateTime.UtcNow, videoPath);
            await context.Services.RecordingSessions.AddAsync(session);

            var host = new FakeHost(context.Services.Recordings)
            {
                ConfirmDeleteAsync = _ => Task.FromResult(true),
            };
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].DeleteCommand.ExecuteAsync(null);

                Assert.Null(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
                Assert.Empty(history.Items);
                Assert.True(history.IsEmpty);
                Assert.False(File.Exists(videoPath));
                Assert.Contains(host.Statuses, status => status.Contains("已删除录制记录", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestOpenClipAsync_MissingFile_Reports() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            await context.Services.RecordingSessions.AddAsync(
                CreateSession("缺片房间", DateTime.UtcNow, Path.Combine(context.TempDirectory, "absent.mkv")));

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].ClipCommand.ExecuteAsync(null);

                Assert.Contains(host.Statuses, status => status.Contains("录像文件不存在，无法剪辑", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestOpenClipAsync_ExistingFile_InvokesHost() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var videoPath = Path.Combine(context.TempDirectory, "clip-me.mkv");
            await File.WriteAllBytesAsync(videoPath, new byte[] { 7 });
            var session = CreateSession("剪辑房间", DateTime.UtcNow, videoPath);
            await context.Services.RecordingSessions.AddAsync(session);

            RecordingSession? opened = null;
            var host = new FakeHost(context.Services.Recordings)
            {
                OpenClipStudioAsync = openedSession =>
                {
                    opened = openedSession;
                    return Task.CompletedTask;
                },
            };
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.Items[0].ClipCommand.ExecuteAsync(null);

                Assert.NotNull(opened);
                Assert.Equal(session.Id, opened!.Id);
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task RequestOpenLiveClipAsync_NoLiveRecording_RefreshesCenter() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RequestOpenLiveClipAsync(Guid.NewGuid());

                Assert.Equal(1, host.RefreshCenterCount);
                Assert.Contains(host.Statuses, status => status.Contains("该录制刚刚结束，正在刷新录制列表", StringComparison.Ordinal));
            }
            finally
            {
                history.Dispose();
            }
        });

    [Fact]
    public Task Dispose_ThenRefresh_LeavesStateUntouched() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            await context.Services.RecordingSessions.AddAsync(CreateSession("释放房间", DateTime.UtcNow));

            var host = new FakeHost(context.Services.Recordings);
            var history = CreateViewModel(context.Services, host);
            history.Dispose();
            await history.RefreshAsync();

            Assert.True(history.IsEmpty);
            Assert.Empty(history.Items);
        });

    [Fact]
    public Task RequestDeleteAsync_HostDisposed_DoesNothing() =>
        ViewModelTestHarness.RunServicesAsync(TestClassName, async context =>
        {
            var session = CreateSession("宿主释放房间", DateTime.UtcNow);
            await context.Services.RecordingSessions.AddAsync(session);

            var host = new FakeHost(context.Services.Recordings)
            {
                ConfirmDeleteAsync = _ => Task.FromResult(true),
                IsDisposed = true,
            };
            var history = CreateViewModel(context.Services, host);
            try
            {
                await history.RefreshAsync();
                await history.RequestDeleteAsync(new RecordingHistoryItemViewModel(session, history));

                Assert.NotNull(await context.Services.RecordingSessions.GetByIdAsync(session.Id));
                Assert.Empty(host.Statuses);
            }
            finally
            {
                history.Dispose();
            }
        });
}
