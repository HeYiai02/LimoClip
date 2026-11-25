using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 「直播间主库」目录子视图模型的契约测试：房间集合/筛选/分组/增删改与开播状态缓存。
/// 宿主边界用 <see cref="FakeRoomCatalogHost"/> 记录调用顺序，真实仓储来自隔离的临时 SQLite 库。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class RoomCatalogViewModelTests
{
    private sealed class FakeRoomCatalogHost : IRoomCatalogHost
    {
        public List<string> Statuses { get; } = new();

        public int MarkDirtyCount { get; private set; }

        public int NotifyWorkspaceSummaryCount { get; private set; }

        public int RefreshRecordingCenterCount { get; private set; }

        public bool StopRecordingResult { get; set; } = true;

        public List<LiveRoom> StopRequests { get; } = new();

        public List<Guid> DetachedRoomIds { get; } = new();

        public int ReflowCount { get; private set; }

        public int SyncDanmakuCount { get; private set; }

        public void SetStatus(string message) => Statuses.Add(message);

        public void MarkDirty() => MarkDirtyCount++;

        public void NotifyWorkspaceSummaryChanged() => NotifyWorkspaceSummaryCount++;

        public void RefreshRecordingCenterLiveStatuses() => RefreshRecordingCenterCount++;

        public Task<bool> TryStopRecordingForDeleteAsync(LiveRoom room, CancellationToken ct)
        {
            StopRequests.Add(room);
            return Task.FromResult(StopRecordingResult);
        }

        public void DetachRoomFromWorkspace(Guid roomId) => DetachedRoomIds.Add(roomId);

        public Task ReflowWorkspaceAsync(CancellationToken ct)
        {
            ReflowCount++;
            return Task.CompletedTask;
        }

        public void SyncDanmakuSubscriptions() => SyncDanmakuCount++;
    }

    private static RoomCatalogViewModel CreateCatalog(
        ViewModelTestHarness.ViewModelTestContext context,
        FakeRoomCatalogHost host) =>
        new(context.Services.Rooms, context.Services.Platforms, context.Services.Credentials, host);

    [Fact]
    public Task ReloadRoomsAsync_LoadsRoomsAndBuildsGroups() =>
        ViewModelTestHarness.RunServicesAsync(nameof(ReloadRoomsAsync_LoadsRoomsAndBuildsGroups), async context =>
        {
            await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", "娱乐");
            await ViewModelTestHarness.AddRoomAsync(context, 2, "Beta", "游戏");
            await ViewModelTestHarness.AddRoomAsync(context, 3, "Gamma");

            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);

            await catalog.ReloadRoomsAsync(CancellationToken.None);

            Assert.Equal(3, catalog.Rooms.Count);
            Assert.Equal(3, catalog.FilteredRooms.Count);
            Assert.Equal(new[] { "全部", "娱乐", "游戏" }, catalog.Groups);
            Assert.Equal(1, host.NotifyWorkspaceSummaryCount);
        });

    [Fact]
    public Task SearchText_FiltersRoomsByAliasAndRoomId() =>
        ViewModelTestHarness.RunServicesAsync(nameof(SearchText_FiltersRoomsByAliasAndRoomId), async context =>
        {
            await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", roomId: "cs_alpha_1");
            await ViewModelTestHarness.AddRoomAsync(context, 2, "Beta", roomId: "cs_beta_2");

            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            catalog.SearchText = "  alpha  ";
            Assert.Single(catalog.FilteredRooms);
            Assert.Equal("Alpha", catalog.FilteredRooms[0].Room.Alias);

            catalog.SearchText = "cs_beta";
            Assert.Single(catalog.FilteredRooms);
            Assert.Equal("Beta", catalog.FilteredRooms[0].Room.Alias);

            catalog.SearchText = "  ";
            Assert.Equal(2, catalog.FilteredRooms.Count);
        });

    [Fact]
    public Task GroupFilter_CombinesWithSearchText() =>
        ViewModelTestHarness.RunServicesAsync(nameof(GroupFilter_CombinesWithSearchText), async context =>
        {
            await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", "游戏", roomId: "cs_game_1");
            await ViewModelTestHarness.AddRoomAsync(context, 2, "Beta", "游戏", roomId: "cs_game_2");
            await ViewModelTestHarness.AddRoomAsync(context, 3, "Gamma", "娱乐", roomId: "cs_fun_3");

            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            catalog.GroupFilter = "游戏";
            Assert.Equal(2, catalog.FilteredRooms.Count);

            catalog.SearchText = "cs_game_1";
            Assert.Single(catalog.FilteredRooms);

            catalog.GroupFilter = "娱乐";
            Assert.Empty(catalog.FilteredRooms);

            catalog.GroupFilter = "全部";
            catalog.SearchText = string.Empty;
            Assert.Equal(3, catalog.FilteredRooms.Count);
        });

    [Fact]
    public Task ReloadRoomsAsync_ResetsUnknownGroupFilterToAll() =>
        ViewModelTestHarness.RunServicesAsync(nameof(ReloadRoomsAsync_ResetsUnknownGroupFilterToAll), async context =>
        {
            await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", "游戏");

            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            catalog.GroupFilter = "不存在的分组";
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            Assert.Equal("全部", catalog.GroupFilter);
        });

    [Fact]
    public Task AddRoomAsync_CustomStream_PersistsAndReportsStatus() =>
        ViewModelTestHarness.RunServicesAsync(nameof(AddRoomAsync_CustomStream_PersistsAndReportsStatus), async context =>
        {
            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);

            var item = await catalog.AddRoomAsync("https://example.com/live/stream-1.flv", " 游戏 ");

            Assert.Single(catalog.Rooms);
            Assert.Single(catalog.FilteredRooms);
            Assert.Equal(PlatformType.CustomStream, item.Room.Platform);
            Assert.StartsWith("cs_", item.Room.RoomId);
            Assert.Equal(1, item.Room.DisplayNumber);
            Assert.StartsWith("#1 ", item.DisplayTitle);
            Assert.Equal("游戏", item.GroupTag);
            Assert.Contains("游戏", catalog.Groups);
            Assert.Equal($"已添加 {item.Room.Alias}", Assert.Single(host.Statuses));
            Assert.Equal(1, host.NotifyWorkspaceSummaryCount);
            Assert.NotNull(catalog.TryGetLiveStatus(item.Room.Id));

            var persisted = await context.Services.Rooms.GetAllAsync();
            Assert.Single(persisted);
            Assert.Equal(item.Room.RawUrl, persisted[0].RawUrl);
        });

    [Fact]
    public Task AddRoomAsync_BlankUrl_ThrowsArgumentException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(AddRoomAsync_BlankUrl_ThrowsArgumentException), async context =>
        {
            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => catalog.AddRoomAsync("   "));

            Assert.Contains("请粘贴直播间链接或房间号。", exception.Message);
            Assert.Empty(catalog.Rooms);
            Assert.Empty(host.Statuses);
        });

    [Fact]
    public Task AddRoomAsync_UnsupportedCustomStreamUrl_ThrowsArgumentException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(AddRoomAsync_UnsupportedCustomStreamUrl_ThrowsArgumentException), async context =>
        {
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => catalog.AddRoomAsync("not-a-stream", null, customStream: true, referer: null, userAgent: null));

            Assert.Contains("自定义清流需要", exception.Message);
            Assert.Empty(catalog.Rooms);
        });

    [Fact]
    public Task AddRoomAsync_AssignsIncrementingDisplayNumbers() =>
        ViewModelTestHarness.RunServicesAsync(nameof(AddRoomAsync_AssignsIncrementingDisplayNumbers), async context =>
        {
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var first = await catalog.AddRoomAsync("https://example.com/live/stream-1.flv");
            var second = await catalog.AddRoomAsync("https://example.com/live/stream-2.flv");

            Assert.Equal(1, first.Room.DisplayNumber);
            Assert.Equal(2, second.Room.DisplayNumber);
            Assert.Equal(2, catalog.Rooms.Count);
            Assert.Equal(2, (await context.Services.Rooms.GetAllAsync()).Count);
        });

    [Fact]
    public Task DeleteRoomAsync_RemovesRoomAndNotifiesHost() =>
        ViewModelTestHarness.RunServicesAsync(nameof(DeleteRoomAsync_RemovesRoomAndNotifiesHost), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            await catalog.DeleteRoomAsync(room.Id);

            Assert.Empty(catalog.Rooms);
            Assert.Empty(catalog.FilteredRooms);
            Assert.Empty(await context.Services.Rooms.GetAllAsync());
            Assert.Equal($"已从主库删除 {room.Alias}", Assert.Single(host.Statuses));
            Assert.Equal(1, host.MarkDirtyCount);
            Assert.Equal(1, host.ReflowCount);
            Assert.Equal(1, host.SyncDanmakuCount);
            Assert.Equal(new[] { room.Id }, host.DetachedRoomIds);
            Assert.Equal(new[] { room.Id }, host.StopRequests.Select(request => request.Id));
        });

    [Fact]
    public Task DeleteRoomAsync_WhenRecordingStopFails_AbortsDeletion() =>
        ViewModelTestHarness.RunServicesAsync(nameof(DeleteRoomAsync_WhenRecordingStopFails_AbortsDeletion), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var host = new FakeRoomCatalogHost { StopRecordingResult = false };
            var catalog = CreateCatalog(context, host);
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            await catalog.DeleteRoomAsync(room.Id);

            Assert.Single(catalog.Rooms);
            Assert.Single(await context.Services.Rooms.GetAllAsync());
            Assert.Empty(host.DetachedRoomIds);
            Assert.Equal(0, host.MarkDirtyCount);
            Assert.Equal(0, host.ReflowCount);
            Assert.Equal(0, host.SyncDanmakuCount);
            Assert.Empty(host.Statuses);
        });

    [Fact]
    public Task DeleteRoomAsync_UnknownRoom_DoesNothing() =>
        ViewModelTestHarness.RunServicesAsync(nameof(DeleteRoomAsync_UnknownRoom_DoesNothing), async context =>
        {
            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);

            await catalog.DeleteRoomAsync(Guid.NewGuid());

            Assert.Empty(host.StopRequests);
            Assert.Empty(host.Statuses);
            Assert.Equal(0, host.MarkDirtyCount);
        });

    [Fact]
    public Task UpdateRoomRecordAsync_UpdatesFieldsAndReportsStreamUrlChange() =>
        ViewModelTestHarness.RunServicesAsync(nameof(UpdateRoomRecordAsync_UpdatesFieldsAndReportsStreamUrlChange), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", roomId: "cs_update_1");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var (updated, streamUrlChanged) = await catalog.UpdateRoomRecordAsync(
                room.Id, "  NewName  ", " 娱乐 ", 5, "https://example.com/live/new-token.flv", "ref", "ua", CancellationToken.None);

            Assert.True(streamUrlChanged);
            Assert.Equal("NewName", updated.Alias);
            Assert.Equal("娱乐", updated.GroupTag);
            Assert.Equal(5, updated.DisplayNumber);

            var persisted = await context.Services.Rooms.GetByIdAsync(room.Id);
            Assert.NotNull(persisted);
            Assert.Equal("https://example.com/live/new-token.flv", persisted!.RawUrl);
            Assert.Equal("NewName", persisted.Alias);

            var (_, sameUrlChanged) = await catalog.UpdateRoomRecordAsync(
                room.Id, "NewName", "娱乐", 5, "https://example.com/live/new-token.flv", null, null, CancellationToken.None);
            Assert.False(sameUrlChanged);
        });

    [Fact]
    public Task UpdateRoomRecordAsync_BlankAlias_ThrowsArgumentException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(UpdateRoomRecordAsync_BlankAlias_ThrowsArgumentException), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => catalog.UpdateRoomRecordAsync(room.Id, "  ", null, 1, null, null, null, CancellationToken.None));

            Assert.Contains("显示名称不能为空。", exception.Message);
        });

    [Fact]
    public Task UpdateRoomRecordAsync_InvalidDisplayNumber_ThrowsArgumentOutOfRange() =>
        ViewModelTestHarness.RunServicesAsync(nameof(UpdateRoomRecordAsync_InvalidDisplayNumber_ThrowsArgumentOutOfRange), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => catalog.UpdateRoomRecordAsync(room.Id, "Alpha", null, 0, null, null, null, CancellationToken.None));

            Assert.Contains("序号必须大于 0。", exception.Message);
        });

    [Fact]
    public Task UpdateRoomRecordAsync_UnknownRoom_ThrowsInvalidOperationException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(UpdateRoomRecordAsync_UnknownRoom_ThrowsInvalidOperationException), async context =>
        {
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => catalog.UpdateRoomRecordAsync(Guid.NewGuid(), "Alpha", null, 1, null, null, null, CancellationToken.None));

            Assert.Contains("直播间不存在。", exception.Message);
        });

    [Fact]
    public Task UpdateRoomRecordAsync_InvalidCustomStreamUrl_ThrowsArgumentException() =>
        ViewModelTestHarness.RunServicesAsync(nameof(UpdateRoomRecordAsync_InvalidCustomStreamUrl_ThrowsArgumentException), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => catalog.UpdateRoomRecordAsync(room.Id, "Alpha", null, 1, "not-a-stream", null, null, CancellationToken.None));

            Assert.Contains("清流地址必须是", exception.Message);
            var persisted = await context.Services.Rooms.GetByIdAsync(room.Id);
            Assert.Equal(room.RawUrl, persisted!.RawUrl);
        });

    [Fact]
    public Task RefreshLiveStatusesAsync_MarksCustomStreamsLiveAndRefreshesRecordingCenter() =>
        ViewModelTestHarness.RunServicesAsync(nameof(RefreshLiveStatusesAsync_MarksCustomStreamsLiveAndRefreshesRecordingCenter), async context =>
        {
            var first = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var second = await ViewModelTestHarness.AddRoomAsync(context, 2, "Beta");
            var host = new FakeRoomCatalogHost();
            var catalog = CreateCatalog(context, host);
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            await catalog.RefreshLiveStatusesAsync();

            Assert.True(catalog.TryGetLiveStatus(first.Id));
            Assert.True(catalog.TryGetLiveStatus(second.Id));
            Assert.All(catalog.Rooms, item => Assert.True(item.IsLive));
            Assert.Equal(1, host.RefreshRecordingCenterCount);
        });

    [Fact]
    public Task KnownLiveStatus_SurvivesReloadAndCanBeAppliedManually() =>
        ViewModelTestHarness.RunServicesAsync(nameof(KnownLiveStatus_SurvivesReloadAndCanBeAppliedManually), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());
            await catalog.ReloadRoomsAsync(CancellationToken.None);

            catalog.RecordLiveStatus(room.Id, false);
            await catalog.ReloadRoomsAsync(CancellationToken.None);
            catalog.ApplyLiveStatuses();

            Assert.False(catalog.Rooms[0].IsLive);
            Assert.False(catalog.TryGetLiveStatus(room.Id));
        });

    [Fact]
    public Task GetRoomOrLoadAsync_UsesCacheAndResolvesAlias() =>
        ViewModelTestHarness.RunServicesAsync(nameof(GetRoomOrLoadAsync_UsesCacheAndResolvesAlias), async context =>
        {
            var room = await ViewModelTestHarness.AddRoomAsync(context, 1, "Alpha", roomId: "cs_alias_1");
            var catalog = CreateCatalog(context, new FakeRoomCatalogHost());

            var loaded = await catalog.GetRoomOrLoadAsync(room.Id);
            var cached = await catalog.GetRoomOrLoadAsync(room.Id);

            Assert.NotNull(loaded);
            Assert.Same(loaded, cached);
            Assert.True(catalog.RoomsById.ContainsKey(room.Id));
            Assert.Equal("Alpha", catalog.ResolveRoomAlias("CS_ALIAS_1"));
            Assert.Null(await catalog.GetRoomOrLoadAsync(Guid.NewGuid()));
        });
}
