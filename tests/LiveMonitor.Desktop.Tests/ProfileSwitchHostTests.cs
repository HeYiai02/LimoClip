using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 方案切换的宿主时序回归：目标是自由布局且宿主结构会变化时，必须先通过视图回调把宿主从 Dock 切到画布，
/// 再翻转模式并重建/填充窗格；否则同一个 MpvPlayerView 会被 Dock 与画布两个 ContentPresenter 争用，
/// 下一次布局测量抛 already has a visual parent 并闪退（堆栈落在 FreeformWorkspaceCanvas.MeasureOverride）。
/// 平铺 → 自由、自由 → 平铺、平铺 → 平铺三条路径的宿主回调调用次数都在这里固定住。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class ProfileSwitchHostTests
{
    private const string TestClassName = nameof(ProfileSwitchHostTests);

    [Fact]
    public Task LoadFreeformProfile_PreparesHostBeforeModeFlips() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            ArrangeRoomsAsync,
            async (_, viewModel) =>
            {
                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_1", "cs_host_2");
                await viewModel.SaveAsAsync("平铺双路");

                viewModel.ToggleFreeformMode();
                Assert.True(viewModel.IsFreeformMode);
                await viewModel.SaveAsAsync("自由双路");

                var host = (IWorkspaceLayoutProfileHost)viewModel;
                await host.LoadProfileAsync(Profile(viewModel, "平铺双路"));
                Assert.False(viewModel.IsFreeformMode);

                var prepareCalls = 0;
                bool? freeformModeDuringPrepare = null;
                var tilesChanged = 0;
                viewModel.WorkspaceTilesChanged += (_, _) => tilesChanged++;
                viewModel.PrepareFreeformHostAsync = () =>
                {
                    prepareCalls++;
                    // 回调必须在模式翻转之前：视图此时仍按平铺状态拆 Dock、解绑播放器。
                    freeformModeDuringPrepare = viewModel.IsFreeformMode;
                    return Task.CompletedTask;
                };

                await host.LoadProfileAsync(Profile(viewModel, "自由双路"));

                Assert.Equal(1, prepareCalls);
                Assert.False(freeformModeDuringPrepare);
                Assert.True(viewModel.IsFreeformMode);
                Assert.NotNull(viewModel.FreeformDocument);
                Assert.True(tilesChanged >= 1, "进入自由布局后必须通知视图填充画布窗格");
            });

    [Fact]
    public Task LoadFreeformProfile_WithDifferentStructure_RepreparesHost() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            ArrangeRoomsAsync,
            async (_, viewModel) =>
            {
                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_1", "cs_host_2");
                await viewModel.SaveAsAsync("平铺双路");

                viewModel.ToggleFreeformMode();
                await viewModel.SaveAsAsync("自由双路");
                await ((IWorkspaceLayoutProfileHost)viewModel).LoadProfileAsync(Profile(viewModel, "平铺双路"));

                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_3");
                viewModel.ToggleFreeformMode();
                Assert.True(viewModel.IsFreeformMode);
                await viewModel.SaveAsAsync("自由三路");

                var prepareCalls = 0;
                bool? freeformModeDuringPrepare = null;
                viewModel.PrepareFreeformHostAsync = () =>
                {
                    prepareCalls++;
                    freeformModeDuringPrepare = viewModel.IsFreeformMode;
                    return Task.CompletedTask;
                };

                await ((IWorkspaceLayoutProfileHost)viewModel).LoadProfileAsync(Profile(viewModel, "自由双路"));

                // 自由方案结构不一致：画布要重建，必须先清空宿主上的播放器再换窗格。
                Assert.Equal(1, prepareCalls);
                Assert.True(freeformModeDuringPrepare);
                Assert.True(viewModel.IsFreeformMode);
            });

    [Fact]
    public Task LoadTiledProfileFromFreeform_LeavesHostBeforeModeFlips() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            ArrangeRoomsAsync,
            async (_, viewModel) =>
            {
                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_1", "cs_host_2");
                await viewModel.SaveAsAsync("平铺双路");

                viewModel.ToggleFreeformMode();
                Assert.True(viewModel.IsFreeformMode);
                await viewModel.SaveAsAsync("自由双路");

                var leaveCalls = 0;
                var prepareCalls = 0;
                viewModel.LeaveFreeformHostAsync = () =>
                {
                    leaveCalls++;
                    // 回调必须在模式翻转之前：视图此时仍能按自由布局状态清理画布宿主。
                    Assert.True(viewModel.IsFreeformMode);
                    viewModel.DetachWorkspacePlayers();
                    return Task.CompletedTask;
                };
                viewModel.PrepareFreeformHostAsync = () =>
                {
                    prepareCalls++;
                    return Task.CompletedTask;
                };

                await ((IWorkspaceLayoutProfileHost)viewModel).LoadProfileAsync(Profile(viewModel, "平铺双路"));

                Assert.Equal(1, leaveCalls);
                Assert.Equal(0, prepareCalls);
                Assert.False(viewModel.IsFreeformMode);
            });

    [Fact]
    public Task LoadTiledProfileFromTiled_DoesNotTouchFreeformHost() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            ArrangeRoomsAsync,
            async (_, viewModel) =>
            {
                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_1", "cs_host_2");
                await viewModel.SaveAsAsync("平铺双路");
                await AddRoomsToWorkspaceAsync(viewModel, "cs_host_3");
                await viewModel.SaveAsAsync("平铺三路");

                var leaveCalls = 0;
                var prepareCalls = 0;
                viewModel.LeaveFreeformHostAsync = () =>
                {
                    leaveCalls++;
                    return Task.CompletedTask;
                };
                viewModel.PrepareFreeformHostAsync = () =>
                {
                    prepareCalls++;
                    return Task.CompletedTask;
                };

                await ((IWorkspaceLayoutProfileHost)viewModel).LoadProfileAsync(Profile(viewModel, "平铺双路"));

                Assert.Equal(0, leaveCalls);
                Assert.Equal(0, prepareCalls);
                Assert.False(viewModel.IsFreeformMode);
                Assert.Equal(2, viewModel.WorkspaceTiles.Count(tile => tile.HasRoom));
            });

    private static async Task ArrangeRoomsAsync(ViewModelTestHarness.ViewModelTestContext context)
    {
        await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", roomId: "cs_host_1");
        await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", roomId: "cs_host_2");
        await ViewModelTestHarness.AddRoomAsync(context, 3, "丙", roomId: "cs_host_3");
    }

    private static async Task AddRoomsToWorkspaceAsync(MainWindowViewModel viewModel, params string[] roomIds)
    {
        foreach (var roomId in roomIds)
        {
            var room = viewModel.Rooms.Single(item => item.Room.RoomId == roomId);
            await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
        }
    }

    private static LiveMonitor.Core.Entities.LayoutProfile Profile(MainWindowViewModel viewModel, string name) =>
        viewModel.Profiles.Single(profile => profile.Name == name);
}
