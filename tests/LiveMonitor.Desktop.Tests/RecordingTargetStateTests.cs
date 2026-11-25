using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

[Collection(AppServicesDatabaseCollection.Name)]
public sealed class RecordingTargetStateTests
{
    /// <summary>
    /// 停止录制时 RecordingManager 先发 SessionCompleted、再发一次最终 Progress；
    /// 后者晚到不能把行内 IsRecording 拉回 true，否则「停止录制」按钮要再点一次才恢复成「立即开录」。
    /// </summary>
    [Fact]
    public async Task ProgressAfterSessionCompleted_DoesNotResurrectRecordingState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();

            var room = new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = 1,
                Platform = PlatformType.Bilibili,
                RoomId = "7734200",
                RawUrl = "https://live.bilibili.com/7734200",
                Alias = "测试房间",
                CreatedAt = DateTime.UtcNow
            };
            var item = new RecordingTargetItemViewModel(room, target: null, viewModel.RecordingCenter);
            Assert.False(item.IsRecording);
            Assert.Equal("立即开录", item.StartStopText);

            var session = new RecordingSession
            {
                Id = Guid.NewGuid(),
                LiveRoomId = room.Id,
                Platform = room.Platform,
                RoomId = room.RoomId,
                RoomDisplayName = item.DisplayTitle,
                Status = RecordingStatus.Completed,
                DurationSeconds = 12,
                FileSizeBytes = 2048,
                DanmakuCount = 3,
                HighlightCount = 1
            };

            item.ApplySessionCompleted(session);
            item.ApplyProgress(new RecordingProgress(
                session.Id,
                TimeSpan.FromSeconds(12),
                session.FileSizeBytes,
                2500d,
                session.DanmakuCount,
                session.HighlightCount));

            Assert.False(item.IsRecording);
            Assert.Equal("立即开录", item.StartStopText);
            // 最终指标仍应被这次收尾进度更新。
            Assert.Equal("00:12", item.DurationText);
            Assert.Equal("3", item.DanmakuCountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 自由布局下点「一键自适应」：必须先回调视图把宿主切回 Dock，再翻模式/重建布局，
    /// 否则仍挂在画布上的播放器被销毁会闪退；平铺状态下则不应触发宿主切换。
    /// </summary>
    [Fact]
    public async Task AutoFitFromFreeform_LeavesHostBeforeRebuild()
    {
        await WithWorkspaceAsync(async (services, viewModel) =>
        {
            await AddRoomsToWorkspaceAsync(services, viewModel, count: 2);

            var leaveCalls = 0;
            viewModel.LeaveFreeformHostAsync = () =>
            {
                leaveCalls++;
                // 回调必须发生在模式翻转之前：视图此时仍能按自由布局状态清理画布宿主。
                Assert.True(viewModel.IsFreeformMode);
                return Task.CompletedTask;
            };

            // 平铺状态下点自适应不应触达宿主切换。
            await viewModel.AutoFitAsync();
            Assert.Equal(0, leaveCalls);

            viewModel.ToggleFreeformMode();
            Assert.True(viewModel.IsFreeformMode);

            await viewModel.AutoFitAsync();

            Assert.Equal(1, leaveCalls);
            Assert.False(viewModel.IsFreeformMode);
            Assert.Equal("自动适配", viewModel.LayoutModeText);
        });
    }

    /// <summary>
    /// 自由布局下点「主副屏布局」：同样先切回 Dock 宿主，再重建为一大一小的主副屏结构。
    /// </summary>
    [Fact]
    public async Task HeroLayoutFromFreeform_LeavesHostAndAppliesHeroLayout()
    {
        await WithWorkspaceAsync(async (services, viewModel) =>
        {
            await AddRoomsToWorkspaceAsync(services, viewModel, count: 3);

            var leaveCalls = 0;
            viewModel.LeaveFreeformHostAsync = () =>
            {
                leaveCalls++;
                Assert.True(viewModel.IsFreeformMode);
                return Task.CompletedTask;
            };

            viewModel.ToggleFreeformMode();
            Assert.True(viewModel.IsFreeformMode);

            await viewModel.ApplyHeroLayoutAsync(startPlayback: false);

            Assert.Equal(1, leaveCalls);
            Assert.False(viewModel.IsFreeformMode);
            Assert.True(viewModel.IsHeroLayout);
            Assert.Equal("主副屏", viewModel.LayoutModeText);
        });
    }

    private static async Task WithWorkspaceAsync(Func<AppServices, MainWindowViewModel, Task> body)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();
            await body(services, viewModel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }
    }

    private static async Task AddRoomsToWorkspaceAsync(AppServices services, MainWindowViewModel viewModel, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var room = new LiveRoom
            {
                DisplayNumber = index + 1,
                Platform = PlatformType.CustomStream,
                RoomId = $"cs_layout_{index + 1}",
                RawUrl = $"https://example.com/layout-{index + 1}.flv",
                Alias = $"布局测试{index + 1}",
                CreatedAt = DateTime.UtcNow
            };
            await services.Rooms.AddAsync(room);
            // startPlayback:false：只建窗格不拉流，避免单测访问网络。
            await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
