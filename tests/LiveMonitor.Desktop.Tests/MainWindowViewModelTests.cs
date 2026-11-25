using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// MainWindowViewModel 基线测试：主库增删/筛选、布局脏标记、方案保存与切换、工作区与视图状态流转。
/// 所有测试房间都使用自定义清流平台，避免初始化时的开播状态轮询访问真实网络。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class MainWindowViewModelTests
{
    private const string TestClassName = nameof(MainWindowViewModelTests);

    [Fact]
    public Task Initialize_EmptyDatabase_ShowsEmptyStateAndCreatesDefaultProfile() =>
        ViewModelTestHarness.RunMainWindowAsync(TestClassName, async (context, viewModel) =>
        {
            Assert.Empty(viewModel.Rooms);
            Assert.Empty(viewModel.FilteredRooms);
            Assert.Equal(new[] { "全部" }, viewModel.Groups);
            Assert.Single(viewModel.Profiles);
            Assert.Equal("默认方案", viewModel.Profiles[0].Name);
            Assert.Equal("默认方案", viewModel.SelectedProfile?.Name);
            Assert.Equal(2, viewModel.GridRows);
            Assert.Equal(2, viewModel.GridColumns);
            Assert.NotNull(viewModel.Layout);
            Assert.Equal(4, viewModel.WorkspaceTiles.Count);
            Assert.All(viewModel.WorkspaceTiles, tile => Assert.True(tile.IsPlaceholder));
            Assert.True(viewModel.IsWorkspaceEmpty);
            Assert.Equal("主库 0 路 · 工作区 2×2", viewModel.RoomCountText);
            Assert.False(viewModel.IsDirty);
            Assert.Single(await context.Services.Profiles.GetAllAsync());
        });

    [Fact]
    public Task Initialize_LoadsRoomsAndBuildsGroups() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 3, "丙", roomId: "cs_room_c");
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", groupTag: "游戏", roomId: "cs_room_a");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", groupTag: "娱乐", roomId: "cs_room_b");
            },
            (context, viewModel) =>
            {
                Assert.Equal(3, viewModel.Rooms.Count);
                Assert.Equal(3, viewModel.FilteredRooms.Count);
                Assert.Equal(new[] { "cs_room_a", "cs_room_b", "cs_room_c" }, viewModel.FilteredRooms.Select(item => item.Room.RoomId).OrderBy(id => id));
                Assert.Equal(3, viewModel.Groups.Count);
                Assert.Equal("全部", viewModel.Groups[0]);
                Assert.Contains("娱乐", viewModel.Groups);
                Assert.Contains("游戏", viewModel.Groups);
                Assert.Equal("主库 3 路 · 工作区 2×2", viewModel.RoomCountText);
                return Task.CompletedTask;
            });

    [Fact]
    public Task SearchText_FiltersByKeyword() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "一号", roomId: "cs_alpha");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "二号", roomId: "cs_beta");
            },
            (context, viewModel) =>
            {
                viewModel.SearchText = "cs_alpha";
                Assert.Single(viewModel.FilteredRooms);
                Assert.Equal("cs_alpha", viewModel.FilteredRooms[0].Room.RoomId);

                viewModel.SearchText = "二号";
                Assert.Single(viewModel.FilteredRooms);
                Assert.Equal("cs_beta", viewModel.FilteredRooms[0].Room.RoomId);

                viewModel.SearchText = "不存在的关键词";
                Assert.Empty(viewModel.FilteredRooms);

                viewModel.SearchText = string.Empty;
                Assert.Equal(2, viewModel.FilteredRooms.Count);
                return Task.CompletedTask;
            });

    [Fact]
    public Task GroupFilter_FiltersByTagAndCombinesWithSearchText() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", groupTag: "游戏", roomId: "cs_game_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", groupTag: "娱乐", roomId: "cs_fun_1");
                await ViewModelTestHarness.AddRoomAsync(context, 3, "丙", groupTag: "游戏", roomId: "cs_game_2");
            },
            (context, viewModel) =>
            {
                Assert.Equal(3, viewModel.FilteredRooms.Count);

                viewModel.GroupFilter = "游戏";
                Assert.Equal(2, viewModel.FilteredRooms.Count);
                Assert.All(viewModel.FilteredRooms, item => Assert.Equal("游戏", item.GroupTag));

                viewModel.SearchText = "cs_game_1";
                Assert.Single(viewModel.FilteredRooms);
                Assert.Equal("cs_game_1", viewModel.FilteredRooms[0].Room.RoomId);

                viewModel.GroupFilter = "娱乐";
                Assert.Empty(viewModel.FilteredRooms);

                viewModel.GroupFilter = "全部";
                viewModel.SearchText = string.Empty;
                Assert.Equal(3, viewModel.FilteredRooms.Count);
                return Task.CompletedTask;
            });

    [Fact]
    public Task AddRoomAsync_CustomStream_AddsRoomAndRebuildsCollections() =>
        ViewModelTestHarness.RunMainWindowAsync(TestClassName, async (context, viewModel) =>
        {
            var added = await viewModel.AddRoomAsync("https://example.com/live/stream-1.flv", groupTag: " 游戏 ");

            Assert.Equal(1, viewModel.Rooms.Count);
            Assert.Single(viewModel.FilteredRooms);
            Assert.Equal(PlatformType.CustomStream, added.Room.Platform);
            Assert.StartsWith("cs_", added.Room.RoomId, StringComparison.Ordinal);
            Assert.Equal(1, added.Room.DisplayNumber);
            Assert.StartsWith("#1 ", added.DisplayTitle, StringComparison.Ordinal);
            Assert.Equal("游戏", added.GroupTag);
            Assert.Equal($"已添加 {added.Room.Alias}", viewModel.StatusText);
            Assert.Contains("游戏", viewModel.Groups);
            Assert.Single(await context.Services.Rooms.GetAllAsync());

            var second = await viewModel.AddRoomAsync("https://example.com/live/stream-2.flv");

            Assert.Equal(2, viewModel.Rooms.Count);
            Assert.Equal(2, second.Room.DisplayNumber);
            Assert.Equal(string.Empty, second.GroupTag);
            Assert.Equal(2, (await context.Services.Rooms.GetAllAsync()).Count);
        });

    [Fact]
    public Task AddRoomAsync_InvalidInput_ThrowsAndKeepsCollectionsEmpty() =>
        ViewModelTestHarness.RunMainWindowAsync(TestClassName, async (context, viewModel) =>
        {
            var statusBefore = viewModel.StatusText;

            var blankError = await Assert.ThrowsAsync<ArgumentException>(
                () => viewModel.AddRoomAsync("   "));
            Assert.Contains("请粘贴直播间链接或房间号。", blankError.Message);

            var urlError = await Assert.ThrowsAsync<ArgumentException>(
                () => viewModel.AddRoomAsync("not-a-stream", groupTag: null, customStream: true, referer: null, userAgent: null));
            Assert.Contains("自定义清流需要", urlError.Message);

            Assert.Empty(viewModel.Rooms);
            Assert.Empty(viewModel.FilteredRooms);
            Assert.Empty(await context.Services.Rooms.GetAllAsync());
            Assert.Equal(statusBefore, viewModel.StatusText);
        });

    [Fact]
    public Task DeleteRoomAsync_RemovesRoomAndRefreshesFilteredLists() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "删除目标", roomId: "cs_delete_me");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "保留目标", roomId: "cs_keep_me");
            },
            async (context, viewModel) =>
            {
                var target = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_delete_me");

                await viewModel.DeleteRoomAsync(target.Id);

                Assert.Single(viewModel.Rooms);
                Assert.Single(viewModel.FilteredRooms);
                Assert.Equal("cs_keep_me", viewModel.FilteredRooms[0].Room.RoomId);
                Assert.Equal($"已从主库删除 {target.Room.Alias}", viewModel.StatusText);
                Assert.True(viewModel.IsDirty);
                Assert.Single(await context.Services.Rooms.GetAllAsync());

                await viewModel.DeleteRoomAsync(Guid.NewGuid());
                Assert.Single(viewModel.Rooms);
            });

    [Fact]
    public Task SaveAndSaveAs_PersistWorkspaceAndResetDirtyFlag() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", roomId: "cs_save_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", roomId: "cs_save_2");
            },
            async (context, viewModel) =>
            {
                var first = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_save_1");
                var second = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_save_2");
                Assert.False(viewModel.IsDirty);

                await viewModel.AddRoomToWorkspaceAsync(first.Id, startPlayback: false);

                Assert.True(viewModel.IsDirty);
                Assert.Equal($"已添加 {first.Room.Alias}", viewModel.StatusText);
                Assert.Contains(viewModel.WorkspaceTiles, tile => tile.LiveRoomId == first.Id);

                await viewModel.SaveAsync();

                Assert.False(viewModel.IsDirty);
                Assert.Equal("已保存方案「默认方案」", viewModel.StatusText);
                Assert.Single(viewModel.Profiles);

                await viewModel.AddRoomToWorkspaceAsync(second.Id, startPlayback: false);

                Assert.True(viewModel.IsDirty);
                Assert.Equal(2, viewModel.WorkspaceTiles.Count(tile => !tile.IsPlaceholder));

                await viewModel.SaveAsAsync("双路方案");

                Assert.False(viewModel.IsDirty);
                Assert.Equal(2, viewModel.Profiles.Count);
                Assert.Equal("双路方案", viewModel.SelectedProfile?.Name);
                Assert.Equal("已另存方案「双路方案」", viewModel.StatusText);
                var persisted = await context.Services.Profiles.GetAllAsync();
                Assert.Equal(2, persisted.Count);
                Assert.Contains(persisted, profile => profile.Name == "双路方案");
            });

    [Fact]
    public Task ApplyHeroLayout_WithThreeRooms_SetsHeroModeAndDirty() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", roomId: "cs_hero_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", roomId: "cs_hero_2");
                await ViewModelTestHarness.AddRoomAsync(context, 3, "丙", roomId: "cs_hero_3");
            },
            async (context, viewModel) =>
            {
                foreach (var room in viewModel.Rooms.ToList())
                {
                    await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
                }

                await viewModel.ApplyHeroLayoutAsync(startPlayback: false);

                Assert.True(viewModel.IsHeroLayout);
                Assert.False(viewModel.IsCustomLayout);
                Assert.Equal("主副屏", viewModel.LayoutModeText);
                Assert.True(viewModel.IsDirty);
                Assert.Equal("已切换为主副屏布局（3 路）", viewModel.StatusText);
                Assert.Equal(3, viewModel.WorkspaceTiles.Count(tile => tile.HasRoom));
                Assert.Equal("主库 3 路 · 工作区 主副屏（3 路）", viewModel.RoomCountText);
            });

    [Fact]
    public Task RemoveRoomFromWorkspace_ClearsWorkspaceAndMarksDirty() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "移出目标", roomId: "cs_remove_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "保留房间", roomId: "cs_remove_2");
            },
            async (context, viewModel) =>
            {
                foreach (var room in viewModel.Rooms.ToList())
                {
                    await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
                }

                var tile = viewModel.WorkspaceTiles.Single(candidate => candidate.RoomId == "cs_remove_1");
                var removedTitle = tile.DisplayTitle;

                await viewModel.RemoveRoomFromWorkspaceAsync(tile);

                Assert.DoesNotContain(viewModel.WorkspaceTiles, candidate => candidate.RoomId == "cs_remove_1");
                Assert.Equal(1, viewModel.WorkspaceTiles.Count(candidate => candidate.HasRoom));
                Assert.Equal($"已移出 {removedTitle}", viewModel.StatusText);
                Assert.True(viewModel.IsDirty);
                Assert.False(viewModel.IsWorkspaceEmpty);
            });

    [Fact]
    public Task ToggleFreeformMode_UpdatesModeTextAndDirtyFlag() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "自由布局房间", roomId: "cs_free_1");
            },
            async (context, viewModel) =>
            {
                Assert.False(viewModel.IsFreeformMode);

                viewModel.ToggleFreeformMode();

                Assert.True(viewModel.IsFreeformMode);
                Assert.Equal("自由布局", viewModel.LayoutModeText);
                Assert.True(viewModel.IsDirty);
                Assert.Contains("已进入自由布局", viewModel.StatusText);
                Assert.False(viewModel.ShowDockWorkspace);

                viewModel.ToggleFreeformMode();

                Assert.False(viewModel.IsFreeformMode);
                Assert.Equal("已退出自由布局：恢复平铺布局。", viewModel.StatusText);
                Assert.Equal("自动适配", viewModel.LayoutModeText);

                var room = viewModel.Rooms.Single();
                await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
                await viewModel.SaveAsync();
                Assert.False(viewModel.IsDirty);

                viewModel.ToggleFreeformMode();

                Assert.True(viewModel.IsFreeformMode);
                Assert.True(viewModel.IsDirty);
                Assert.Contains("已进入自由布局", viewModel.StatusText);
            });

    [Fact]
    public Task SwitchViews_TogglesVisibilityAndLoadsRecordingCenter() =>
        ViewModelTestHarness.RunMainWindowAsync(TestClassName, async (context, viewModel) =>
        {
            Assert.False(viewModel.IsRecordingCenterVisible);
            Assert.False(viewModel.IsClipStudioVisible);

            await viewModel.SwitchToRecordingCenterCommand.ExecuteAsync(null);

            Assert.True(viewModel.IsRecordingCenterVisible);
            Assert.False(viewModel.IsClipStudioVisible);
            Assert.True(viewModel.RecordingCenter.IsTargetsEmpty);
            Assert.True(viewModel.RecordingCenter.IsHistoryEmpty);
            Assert.Equal("已刷新：0 个直播间 · 0 条历史录像", viewModel.RecordingCenter.StatusText);

            viewModel.SwitchToWorkspaceCommand.Execute(null);

            Assert.False(viewModel.IsRecordingCenterVisible);
            Assert.False(viewModel.IsClipStudioVisible);
        });

    [Fact]
    public Task ProfileSwitch_WhenDirtyAndCancelled_RollsBackSelection() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", roomId: "cs_switch_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", roomId: "cs_switch_2");
            },
            async (context, viewModel) =>
            {
                var first = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_switch_1");
                var second = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_switch_2");
                await viewModel.AddRoomToWorkspaceAsync(first.Id, startPlayback: false);
                await viewModel.SaveAsAsync("方案B");

                Assert.Equal("方案B", viewModel.SelectedProfile?.Name);
                await viewModel.AddRoomToWorkspaceAsync(second.Id, startPlayback: false);
                Assert.True(viewModel.IsDirty);

                var selectedBefore = viewModel.SelectedProfile;
                var defaultProfile = viewModel.Profiles.Single(profile => profile.Name == "默认方案");
                var confirmCount = 0;
                viewModel.ConfirmProfileSwitchAsync = () =>
                {
                    confirmCount++;
                    return Task.FromResult(ProfileSwitchChoice.Cancel);
                };

                viewModel.SelectedProfile = defaultProfile;

                await ViewModelTestHarness.WaitUntilAsync(
                    () => ReferenceEquals(viewModel.SelectedProfile, selectedBefore),
                    "取消切换方案后选中项应回滚");
                Assert.Equal(1, confirmCount);
                Assert.Equal("方案B", viewModel.SelectedProfile?.Name);
                Assert.True(viewModel.IsDirty);
            });

    [Fact]
    public Task ProfileSwitch_WhenDirtyAndDiscarded_LoadsProfileAndClearsDirty() =>
        ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                await ViewModelTestHarness.AddRoomAsync(context, 1, "甲", roomId: "cs_discard_1");
                await ViewModelTestHarness.AddRoomAsync(context, 2, "乙", roomId: "cs_discard_2");
            },
            async (context, viewModel) =>
            {
                var first = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_discard_1");
                var second = viewModel.Rooms.Single(item => item.Room.RoomId == "cs_discard_2");
                await viewModel.AddRoomToWorkspaceAsync(first.Id, startPlayback: false);
                await viewModel.SaveAsAsync("方案C");
                await viewModel.AddRoomToWorkspaceAsync(second.Id, startPlayback: false);
                Assert.True(viewModel.IsDirty);

                var defaultProfile = viewModel.Profiles.Single(profile => profile.Name == "默认方案");
                viewModel.ConfirmProfileSwitchAsync = () => Task.FromResult(ProfileSwitchChoice.Discard);

                viewModel.SelectedProfile = defaultProfile;

                await ViewModelTestHarness.WaitUntilAsync(
                    () => viewModel.SelectedProfile?.Name == "默认方案" && !viewModel.IsDirty,
                    "丢弃改动后应载入默认方案并清除脏标记");
                Assert.Equal("默认方案", viewModel.SelectedProfile?.Name);
                Assert.False(viewModel.IsDirty);
                Assert.StartsWith("已载入方案「默认方案」", viewModel.StatusText, StringComparison.Ordinal);
            });
}
