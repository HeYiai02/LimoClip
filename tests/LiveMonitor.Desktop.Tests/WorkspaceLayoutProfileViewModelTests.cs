using LiveMonitor.Core.Entities;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 顶部布局方案档案子视图模型的契约测试：方案初始化、保存/另存为、dirty 切换确认与选中项回滚。
/// 布局树加载/采集由宿主完成，测试用 <see cref="FakeWorkspaceLayoutProfileHost"/> 记录宿主调用。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class WorkspaceLayoutProfileViewModelTests
{
    private sealed class FakeWorkspaceLayoutProfileHost : IWorkspaceLayoutProfileHost
    {
        public WorkspaceLayoutSnapshot? Snapshot { get; set; }

        public List<LayoutProfile> LoadedProfiles { get; } = new();

        public List<string> Statuses { get; } = new();

        public int CaptureSnapshotCount { get; private set; }

        public int ClearSnapshotCount { get; private set; }

        public WorkspaceLayoutSnapshot? CaptureSnapshot()
        {
            CaptureSnapshotCount++;
            return Snapshot;
        }

        public Task LoadProfileAsync(LayoutProfile profile)
        {
            LoadedProfiles.Add(profile);
            return Task.CompletedTask;
        }

        public void ClearCustomLayoutSnapshot() => ClearSnapshotCount++;

        public void SetStatus(string message) => Statuses.Add(message);
    }

    private static WorkspaceLayoutProfileViewModel CreateViewModel(
        ViewModelTestHarness.ViewModelTestContext context,
        FakeWorkspaceLayoutProfileHost host,
        Func<string>? defaultLayoutJsonFactory = null) =>
        new(context.Services.Profiles, defaultLayoutJsonFactory ?? (() => "{\"root\":{}}"), host);

    private static WorkspaceLayoutSnapshot CreateSnapshot(params RoomTileViewModel[] tiles) =>
        new("{\"layout\":1}", tiles, IsCustomLayout: false, IsFreeformMode: false);

    private static async Task SeedProfilesAsync(
        ViewModelTestHarness.ViewModelTestContext context,
        params string[] names)
    {
        foreach (var name in names)
        {
            await context.Services.Profiles.SaveAsync(new LayoutProfile
            {
                Name = name,
                DockLayoutJson = "{\"seeded\":true}"
            });
        }
    }

    [Fact]
    public Task InitializeAsync_EmptyRepository_CreatesAndLoadsDefaultProfile() =>
        ViewModelTestHarness.RunServicesAsync(nameof(InitializeAsync_EmptyRepository_CreatesAndLoadsDefaultProfile), async context =>
        {
            var factoryCalls = 0;
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host, () =>
            {
                factoryCalls++;
                return "{\"default\":true}";
            });

            await viewModel.InitializeAsync();

            var profile = Assert.Single(viewModel.Profiles);
            Assert.Equal("默认方案", profile.Name);
            Assert.Equal("{\"default\":true}", profile.DockLayoutJson);
            Assert.Equal(1, factoryCalls);
            Assert.Null(viewModel.SelectedProfile);

            var persisted = await context.Services.Profiles.GetAllAsync();
            Assert.Single(persisted);
            Assert.Equal("默认方案", persisted[0].Name);
        });

    [Fact]
    public Task InitializeAsync_ExistingProfiles_DoesNotCreateDefault() =>
        ViewModelTestHarness.RunServicesAsync(nameof(InitializeAsync_ExistingProfiles_DoesNotCreateDefault), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var factoryCalls = 0;
            var viewModel = CreateViewModel(context, new FakeWorkspaceLayoutProfileHost(), () =>
            {
                factoryCalls++;
                return "{}";
            });

            await viewModel.InitializeAsync();

            Assert.Equal(new[] { "方案A", "方案B" }, viewModel.Profiles.Select(profile => profile.Name));
            Assert.Equal(0, factoryCalls);
        });

    [Fact]
    public Task LoadInitialProfileAsync_SelectsFirstAndLoadsThroughHost() =>
        ViewModelTestHarness.RunServicesAsync(nameof(LoadInitialProfileAsync_SelectsFirstAndLoadsThroughHost), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();

            await viewModel.LoadInitialProfileAsync();

            Assert.Same(viewModel.Profiles[0], viewModel.SelectedProfile);
            Assert.Same(viewModel.Profiles[0], Assert.Single(host.LoadedProfiles));
            Assert.Equal(0, host.ClearSnapshotCount);
        });

    [Fact]
    public Task LoadInitialProfileAsync_WhenProfilesEmpty_DoesNothing() =>
        ViewModelTestHarness.RunServicesAsync(nameof(LoadInitialProfileAsync_WhenProfilesEmpty_DoesNothing), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);

            await viewModel.LoadInitialProfileAsync();

            Assert.Null(viewModel.SelectedProfile);
            Assert.Empty(host.LoadedProfiles);
        });

    [Fact]
    public Task SelectedProfile_BeforeInitialization_DoesNotTriggerLoad() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SelectedProfile_BeforeInitialization_DoesNotTriggerLoad), context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            var profile = new LayoutProfile { Name = "未初始化方案" };

            viewModel.SelectedProfile = profile;

            Assert.Same(profile, viewModel.SelectedProfile);
            Assert.Empty(host.LoadedProfiles);
            return Task.CompletedTask;
        });

    [Fact]
    public Task SaveAsync_CapturesSnapshotAndPersistsProfile() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SaveAsync_CapturesSnapshotAndPersistsProfile), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();

            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var tile = new RoomTileViewModel("pane-1", room);
            host.Snapshot = new WorkspaceLayoutSnapshot("{\"layout\":2}", new[] { tile }, IsCustomLayout: true, IsFreeformMode: false);
            viewModel.MarkDirty();

            await viewModel.SaveAsync();

            var profile = Assert.Single(viewModel.Profiles);
            Assert.Equal("{\"layout\":2}", profile.DockLayoutJson);
            Assert.True(profile.IsCustomLayout);
            var slot = Assert.Single(profile.Slots);
            Assert.Equal("pane-1", slot.PaneId);
            Assert.Equal(tile.LiveRoomId, slot.LiveRoomId);
            Assert.False(viewModel.IsDirty);
            Assert.Same(profile, viewModel.SelectedProfile);
            Assert.Equal("已保存方案「默认方案」", Assert.Single(host.Statuses));
            Assert.Equal(1, host.CaptureSnapshotCount);

            var persisted = await context.Services.Profiles.GetAllAsync();
            Assert.Equal(profile.DockLayoutJson, Assert.Single(persisted).DockLayoutJson);
        });

    [Fact]
    public Task SaveAsync_FreeformMode_PersistsCustomLayoutFlag() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SaveAsync_FreeformMode_PersistsCustomLayoutFlag), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();
            host.Snapshot = new WorkspaceLayoutSnapshot("{\"freeform\":true}", Array.Empty<RoomTileViewModel>(), IsCustomLayout: false, IsFreeformMode: true);

            await viewModel.SaveAsync();

            Assert.True(Assert.Single(viewModel.Profiles).IsCustomLayout);
        });

    [Fact]
    public Task SaveAsync_WithoutSnapshot_DoesNothing() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SaveAsync_WithoutSnapshot_DoesNothing), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            viewModel.MarkDirty();

            await viewModel.SaveAsync();

            Assert.Empty(viewModel.Profiles);
            Assert.Empty(host.Statuses);
            Assert.True(viewModel.IsDirty);
        });

    [Fact]
    public Task SaveAsAsync_CreatesNewProfileAndSelectsIt() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SaveAsAsync_CreatesNewProfileAndSelectsIt), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            host.Snapshot = CreateSnapshot();
            viewModel.MarkDirty();

            await viewModel.SaveAsAsync("  双路方案  ");

            Assert.Equal(2, viewModel.Profiles.Count);
            Assert.Equal("双路方案", viewModel.SelectedProfile?.Name);
            Assert.False(viewModel.IsDirty);
            Assert.Equal("已另存方案「双路方案」", host.Statuses.Last());

            var persisted = await context.Services.Profiles.GetAllAsync();
            Assert.Equal(2, persisted.Count);
            Assert.Contains(persisted, profile => profile.Name == "双路方案");
        });

    [Fact]
    public Task SaveAsAsync_BlankName_ThrowsArgumentException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SaveAsAsync_BlankName_ThrowsArgumentException), async context =>
        {
            var host = new FakeWorkspaceLayoutProfileHost { Snapshot = CreateSnapshot() };
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => viewModel.SaveAsAsync("   "));

            Assert.Contains("方案名不能为空。", exception.Message);
            Assert.Single(viewModel.Profiles);
            Assert.Single(await context.Services.Profiles.GetAllAsync());
        });

    [Fact]
    public Task SwitchProfile_WhenNotDirty_LoadsImmediatelyWithoutConfirm() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SwitchProfile_WhenNotDirty_LoadsImmediatelyWithoutConfirm), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();

            var confirmCalls = 0;
            viewModel.ConfirmProfileSwitchAsync = () =>
            {
                confirmCalls++;
                return Task.FromResult(ProfileSwitchChoice.Cancel);
            };

            var target = viewModel.Profiles[1];
            viewModel.SelectedProfile = target;
            await ViewModelTestHarness.WaitUntilAsync(() => host.LoadedProfiles.Count == 1, "未脏状态下直接加载目标方案");

            Assert.Equal(0, confirmCalls);
            Assert.Same(target, Assert.Single(host.LoadedProfiles));
            Assert.Same(target, viewModel.SelectedProfile);
            Assert.Equal(1, host.ClearSnapshotCount);
        });

    [Fact]
    public Task SwitchProfile_WhenDirtyAndCancelled_RollsBackSelection() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SwitchProfile_WhenDirtyAndCancelled_RollsBackSelection), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();
            var loaded = viewModel.Profiles[0];
            host.LoadedProfiles.Clear();
            viewModel.MarkDirty();

            var confirmCalls = 0;
            viewModel.ConfirmProfileSwitchAsync = () =>
            {
                confirmCalls++;
                return Task.FromResult(ProfileSwitchChoice.Cancel);
            };

            viewModel.SelectedProfile = viewModel.Profiles[1];

            await ViewModelTestHarness.WaitUntilAsync(
                () => ReferenceEquals(viewModel.SelectedProfile, loaded),
                "取消切换后选中项回滚到已加载方案");
            Assert.Equal(1, confirmCalls);
            Assert.Empty(host.LoadedProfiles);
            Assert.True(viewModel.IsDirty);
        });

    [Fact]
    public Task SwitchProfile_WhenDirtyAndDiscarded_LoadsTargetAndClearsSnapshot() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SwitchProfile_WhenDirtyAndDiscarded_LoadsTargetAndClearsSnapshot), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();
            host.LoadedProfiles.Clear();
            viewModel.MarkDirty();
            viewModel.ConfirmProfileSwitchAsync = () => Task.FromResult(ProfileSwitchChoice.Discard);

            var target = viewModel.Profiles[1];
            viewModel.SelectedProfile = target;
            await ViewModelTestHarness.WaitUntilAsync(() => host.LoadedProfiles.Count == 1, "放弃修改后加载目标方案");

            Assert.Same(target, Assert.Single(host.LoadedProfiles));
            Assert.Same(target, viewModel.SelectedProfile);
            Assert.Equal(1, host.ClearSnapshotCount);
        });

    [Fact]
    public Task SwitchProfile_WhenDirtyAndSaveChoice_SavesThenLoads() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SwitchProfile_WhenDirtyAndSaveChoice_SavesThenLoads), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost { Snapshot = CreateSnapshot() };
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();
            host.LoadedProfiles.Clear();
            viewModel.MarkDirty();
            viewModel.ConfirmProfileSwitchAsync = () => Task.FromResult(ProfileSwitchChoice.Save);

            var target = viewModel.Profiles[1];
            viewModel.SelectedProfile = target;
            await ViewModelTestHarness.WaitUntilAsync(() => host.LoadedProfiles.Count == 1, "保存并切换后加载目标方案");

            Assert.Same(target, Assert.Single(host.LoadedProfiles));
            Assert.Contains("已保存方案「方案B」", host.Statuses);
            Assert.False(viewModel.IsDirty);
            Assert.Equal(1, host.CaptureSnapshotCount);
        });

    [Fact]
    public Task NotifyProfileLoaded_SuppressesReloadOfSameProfile() =>
        ViewModelTestHarness.RunServicesAsync(nameof(NotifyProfileLoaded_SuppressesReloadOfSameProfile), async context =>
        {
            await SeedProfilesAsync(context, "方案A", "方案B");
            var host = new FakeWorkspaceLayoutProfileHost();
            var viewModel = CreateViewModel(context, host);
            await viewModel.InitializeAsync();
            await viewModel.LoadInitialProfileAsync();
            host.LoadedProfiles.Clear();
            viewModel.MarkDirty();

            var confirmCalls = 0;
            viewModel.ConfirmProfileSwitchAsync = () =>
            {
                confirmCalls++;
                return Task.FromResult(ProfileSwitchChoice.Cancel);
            };

            var target = viewModel.Profiles[1];
            viewModel.NotifyProfileLoaded(target);
            viewModel.SelectedProfile = target;

            Assert.Equal(0, confirmCalls);
            Assert.Empty(host.LoadedProfiles);
            Assert.Same(target, viewModel.SelectedProfile);
        });

    [Fact]
    public Task DirtyState_MarkDirtyMarkCleanAndRestoreDirty_ToggleAsExpected() =>
        ViewModelTestHarness.RunServicesAsync(nameof(DirtyState_MarkDirtyMarkCleanAndRestoreDirty_ToggleAsExpected), context =>
        {
            var viewModel = CreateViewModel(context, new FakeWorkspaceLayoutProfileHost());

            Assert.False(viewModel.IsDirty);
            viewModel.MarkDirty();
            Assert.True(viewModel.IsDirty);
            viewModel.MarkClean();
            Assert.False(viewModel.IsDirty);
            viewModel.RestoreDirty(true);
            Assert.True(viewModel.IsDirty);
            viewModel.RestoreDirty(false);
            Assert.False(viewModel.IsDirty);
            return Task.CompletedTask;
        });
}
