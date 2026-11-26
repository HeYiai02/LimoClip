using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Platforms;
using LiveMonitor.Platforms.CustomStream;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 房间目录子视图模型与窗口宿主的边界：工作区清理、录制中止、方案脏标记与状态提示
/// 仍由 <see cref="MainWindowViewModel"/> 负责，子 VM 只编排主库数据与筛选状态。
/// </summary>
internal interface IRoomCatalogHost
{
    /// <summary>写入状态栏文案（主 VM 会同时弹 Toast 并通知状态栏子 VM）。</summary>
    void SetStatus(string message);

    /// <summary>标记当前方案有未保存修改。</summary>
    void MarkDirty();

    /// <summary>主库/工作区数量变化后刷新底部状态栏计数。</summary>
    void NotifyWorkspaceSummaryChanged();

    /// <summary>录制中心已创建时同步开播状态（让「正在直播」的房间按排序规则上浮）。</summary>
    void RefreshRecordingCenterLiveStatuses();

    /// <summary>删除直播间前停止其录制；返回 false 表示停止失败、必须中止删除。</summary>
    Task<bool> TryStopRecordingForDeleteAsync(LiveRoom room, CancellationToken ct);

    /// <summary>把房间从工作区窗格中解绑并清空（主库记录保留）。</summary>
    void DetachRoomFromWorkspace(Guid roomId);

    /// <summary>房间被删除后按自动布局重排工作区。</summary>
    Task ReflowWorkspaceAsync(CancellationToken ct);

    /// <summary>让弹幕订阅与工作区中的房间保持一致。</summary>
    void SyncDanmakuSubscriptions();
}

/// <summary>
/// 左侧「直播间主库」目录子视图模型：房间集合、搜索/分组筛选、添加/更新/删除与开播状态轮询。
/// 从 <see cref="MainWindowViewModel"/> 抽离后仍由主 VM 持有并通过属性转发对外暴露，
/// 既有绑定路径、代码后置与测试无需改动。本类不引用 Avalonia 类型（计时器由主 VM 驱动）。
/// </summary>
public sealed class RoomCatalogViewModel : ObservableObject, IDisposable
{
    private readonly IRoomRepository _rooms;
    private readonly PlatformFactory _platforms;
    private readonly ICredentialStore _credentials;
    private readonly IRoomCatalogHost _host;

    // 主库实体缓存：主 VM 的布局/播放器路径按 roomId 直接取用，避免每次回查数据库。
    private readonly Dictionary<Guid, LiveRoom> _roomById = new();

    // 主库在线状态缓存：null=未知，true=直播中，false=未开播/失败。
    private readonly ConcurrentDictionary<Guid, bool?> _knownLiveStatus = new();

    private string _searchText = string.Empty;
    private string _groupFilter = RoomCatalogCoordinator.AllGroupLabel;
    private bool _roomStatusRefreshing;
    private bool _disposed;

    private static readonly TimeSpan RoomStatusCheckTimeout = TimeSpan.FromSeconds(10);

    internal RoomCatalogViewModel(
        IRoomRepository rooms,
        PlatformFactory platforms,
        ICredentialStore credentials,
        IRoomCatalogHost host)
    {
        _rooms = rooms;
        _platforms = platforms;
        _credentials = credentials;
        _host = host;
    }

    public ObservableCollection<RoomListItemViewModel> Rooms { get; } = new();

    public ObservableCollection<RoomListItemViewModel> FilteredRooms { get; } = new();

    /// <summary>侧栏分组 pills，第一项固定为「全部」，其余按现有房间的分组动态生成。</summary>
    public ObservableCollection<string> Groups { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilteredRooms();
            }
        }
    }

    public string GroupFilter
    {
        get => _groupFilter;
        set
        {
            if (SetProperty(ref _groupFilter, value))
            {
                RefreshFilteredRooms();
            }
        }
    }

    /// <summary>主库房间实体缓存；主 VM 的布局/播放器路径只读访问。</summary>
    internal IReadOnlyDictionary<Guid, LiveRoom> RoomsById => _roomById;

    internal bool? TryGetLiveStatus(Guid roomId) =>
        _knownLiveStatus.TryGetValue(roomId, out var live) ? live : null;

    internal void RecordLiveStatus(Guid roomId, bool? live) => _knownLiveStatus[roomId] = live;

    /// <summary>把缓存中的开播状态写回主库列表项（只写已知状态，不覆盖未知项）。</summary>
    internal void ApplyLiveStatuses()
    {
        foreach (var item in Rooms)
        {
            if (_knownLiveStatus.TryGetValue(item.Id, out var live))
            {
                item.SetLiveStatus(live);
            }
        }
    }

    internal string? ResolveRoomAlias(string roomId) =>
        _roomById.Values.FirstOrDefault(room =>
            string.Equals(room.RoomId, roomId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(room.Alias))?.Alias;

    /// <summary>取房间实体：优先命中缓存，未命中回查仓储并写回缓存。</summary>
    internal async Task<LiveRoom?> GetRoomOrLoadAsync(Guid roomId, CancellationToken ct = default)
    {
        if (_roomById.TryGetValue(roomId, out var cached))
        {
            return cached;
        }

        var room = await _rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true);
        if (room is not null)
        {
            _roomById[room.Id] = room;
        }

        return room;
    }

    public Task<RoomListItemViewModel> AddRoomAsync(string url, string? groupTag = null, CancellationToken ct = default)
    {
        return AddRoomCoreAsync(url, groupTag, customStream: false, referer: null, userAgent: null, ct);
    }

    /// <summary>
    /// 添加房间（V0.6-A：<paramref name="customStream"/> 为 true 时跳过平台识别，按自定义清流直链保存）。
    /// 清流使用 <c>cs_xxxxxxxx</c> 固定短 ID 作为 RoomId，真实地址存 RawUrl，换 Token 时只改 RawUrl 保留槽位。
    /// </summary>
    public Task<RoomListItemViewModel> AddRoomAsync(
        string url,
        string? groupTag,
        bool customStream,
        string? referer,
        string? userAgent,
        CancellationToken ct = default)
    {
        return AddRoomCoreAsync(url, groupTag, customStream, referer, userAgent, ct);
    }

    private async Task<RoomListItemViewModel> AddRoomCoreAsync(
        string url,
        string? groupTag,
        bool customStream,
        string? referer,
        string? userAgent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException(RoomCatalogStatusText.MissingUrl, nameof(url));
        }

        IPlatformProvider provider;
        string parsedRoomId;
        string? cookie;
        RoomStatus status;
        string? customHeadersJson = null;
        var trimmedUrl = url.Trim();

        if (customStream || _platforms.GetProvider(trimmedUrl) is CustomStreamProvider)
        {
            // 识别为直链/清流时一律按自定义清流处理（UI 自动勾选之外的第二道防线），
            // 因为清流没有房间号概念，走平台房间查询会得到不存在的 roomId。
            if (!CustomStreamProvider.IsSupportedDirectUrl(trimmedUrl))
            {
                throw new ArgumentException(RoomCatalogStatusText.InvalidCustomStreamUrl, nameof(url));
            }

            provider = _platforms.GetProvider(PlatformType.CustomStream);
            if (provider is not CustomStreamProvider customProvider)
            {
                throw new InvalidOperationException(RoomCatalogStatusText.CustomProviderMissing);
            }

            parsedRoomId = customProvider.ExtractRoomId(trimmedUrl);
            cookie = null;
            // 直接把完整 URL 作为 roomId 传进去可绕过数据库查询，添加阶段无需先落库。
            status = await customProvider.GetRoomStatusAsync(trimmedUrl, null, ct).ConfigureAwait(true);
            customHeadersJson = CustomStreamProvider.BuildHeadersJson(referer, userAgent);
            customStream = true;
        }
        else
        {
            provider = _platforms.GetProvider(trimmedUrl);
            parsedRoomId = provider.ExtractRoomId(trimmedUrl);
            cookie = await _credentials.GetCookieAsync(provider.Platform, ct).ConfigureAwait(true);
            status = await provider.GetRoomStatusAsync(parsedRoomId, cookie, ct).ConfigureAwait(true);
        }

        var canonicalRoomId = customStream
            ? parsedRoomId
            : string.IsNullOrWhiteSpace(status.RoomId) ? parsedRoomId : status.RoomId;

        if (await _rooms.ExistsAsync(provider.Platform, canonicalRoomId, ct).ConfigureAwait(true))
        {
            throw new InvalidOperationException(RoomCatalogStatusText.DuplicateRoom);
        }

        var room = new LiveRoom
        {
            DisplayNumber = await _rooms.GetNextDisplayNumberAsync(ct).ConfigureAwait(true),
            Platform = provider.Platform,
            RoomId = canonicalRoomId,
            RawUrl = trimmedUrl,
            Alias = string.IsNullOrWhiteSpace(status.AnchorName)
                ? $"{provider.Platform}_{canonicalRoomId}"
                : status.AnchorName,
            GroupTag = string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim(),
            CustomHeadersJson = customHeadersJson,
            CreatedAt = DateTime.UtcNow
        };

        _knownLiveStatus[room.Id] = status.IsLive;
        await _rooms.AddAsync(room, ct).ConfigureAwait(true);
        await ReloadRoomsAsync(ct).ConfigureAwait(true);
        _host.SetStatus(RoomCatalogStatusText.Added(room.Alias));

        var item = Rooms.FirstOrDefault(candidate => candidate.Id == room.Id);
        return item ?? new RoomListItemViewModel(room);
    }

    /// <summary>
    /// 更新房间持久化字段（显示名/分组/序号，自定义清流还可换地址与请求头），返回更新后的实体。
    /// 工作区窗格的元数据同步与播放器重启由主 VM 完成，保证视图层职责留在窗口宿主。
    /// </summary>
    internal async Task<(LiveRoom Room, bool StreamUrlChanged)> UpdateRoomRecordAsync(
        Guid roomId,
        string alias,
        string? groupTag,
        int displayNumber,
        string? rawUrl,
        string? referer,
        string? userAgent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException(RoomCatalogStatusText.AliasRequired, nameof(alias));
        }

        if (displayNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayNumber), RoomCatalogStatusText.DisplayNumberOutOfRange);
        }

        var room = await _rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true)
                   ?? throw new InvalidOperationException(RoomCatalogStatusText.RoomNotFound);
        room.Alias = alias.Trim();
        room.GroupTag = string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim();
        room.DisplayNumber = displayNumber;

        var streamUrlChanged = false;
        if (room.Platform == PlatformType.CustomStream && rawUrl is not null)
        {
            var trimmedUrl = rawUrl.Trim();
            if (!CustomStreamProvider.IsSupportedDirectUrl(trimmedUrl))
            {
                throw new ArgumentException(RoomCatalogStatusText.InvalidStreamUrl, nameof(rawUrl));
            }

            streamUrlChanged = !string.Equals(room.RawUrl, trimmedUrl, StringComparison.Ordinal);
            room.RawUrl = trimmedUrl;
            room.CustomHeadersJson = CustomStreamProvider.BuildHeadersJson(referer, userAgent);
        }

        await _rooms.UpdateAsync(room, ct).ConfigureAwait(true);
        return (room, streamUrlChanged);
    }

    public async Task DeleteRoomAsync(Guid roomId, CancellationToken ct = default)
    {
        var room = await _rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true);
        if (room is null)
        {
            return;
        }

        // 删除直播间前先停止该房间的录制：历史录像保留（LiveRoomId 由外键置空），
        // 但不允许在直播间被删除后仍有一个正在写入的录制会话。
        if (!await _host.TryStopRecordingForDeleteAsync(room, ct).ConfigureAwait(true))
        {
            return;
        }

        _host.DetachRoomFromWorkspace(roomId);

        await _rooms.DeleteAsync(roomId, ct).ConfigureAwait(true);
        _roomById.Remove(roomId);
        _knownLiveStatus.TryRemove(roomId, out _);
        await ReloadRoomsAsync(ct).ConfigureAwait(true);
        await _host.ReflowWorkspaceAsync(ct).ConfigureAwait(true);
        _host.SyncDanmakuSubscriptions();
        _host.MarkDirty();
        _host.SetStatus(RoomCatalogStatusText.Removed(room.Alias));
    }

    internal async Task ReloadRoomsAsync(CancellationToken ct)
    {
        var rooms = await _rooms.GetAllAsync(ct).ConfigureAwait(true);
        _roomById.Clear();
        Rooms.Clear();

        foreach (var room in rooms)
        {
            _roomById[room.Id] = room;
            var item = new RoomListItemViewModel(room);
            if (_knownLiveStatus.TryGetValue(room.Id, out var live))
            {
                item.SetLiveStatus(live);
            }

            Rooms.Add(item);
        }

        RefreshFilteredRooms();
        RebuildGroups();
        _host.NotifyWorkspaceSummaryChanged();
    }

    private void RefreshFilteredRooms()
    {
        var filtered = RoomCatalogCoordinator.Filter(Rooms, SearchText, GroupFilter);
        FilteredRooms.Clear();
        foreach (var room in filtered)
        {
            FilteredRooms.Add(room);
        }
    }

    private void RebuildGroups()
    {
        var groups = RoomCatalogCoordinator.BuildGroups(Rooms);

        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        if (!RoomCatalogCoordinator.IsKnownGroup(Groups, GroupFilter))
        {
            GroupFilter = RoomCatalogCoordinator.AllGroupLabel;
        }
    }

    /// <summary>
    /// 轮询主库所有房间的开播状态，避免「只有放进工作区才显示状态」。
    /// 计时器由主 VM 持有（Avalonia DispatcherTimer 留在窗口层），这里只负责一轮查询。
    /// </summary>
    internal async Task RefreshLiveStatusesAsync()
    {
        if (_disposed || _roomStatusRefreshing)
        {
            return;
        }

        _roomStatusRefreshing = true;
        try
        {
            var rooms = Rooms.Select(item => item.Room).ToList();
            using var gate = new SemaphoreSlim(3);

            // 有上限的并发：15 路房间若逐个等 10s 超时会拖到两分钟以上。
            var tasks = rooms.Select(async room =>
            {
                await gate.WaitAsync().ConfigureAwait(true);
                try
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (room.Platform == PlatformType.CustomStream)
                    {
                        // 清流没有「开播」概念，按在线处理，避免长期显示为未知。
                        _knownLiveStatus[room.Id] = true;
                        return;
                    }

                    var live = await TryQueryRoomLiveAsync(room).ConfigureAwait(true);
                    if (live is not null)
                    {
                        _knownLiveStatus[room.Id] = live;
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        finally
        {
            _roomStatusRefreshing = false;
        }

        ApplyLiveStatuses();
        _host.RefreshRecordingCenterLiveStatuses();
    }

    /// <summary>查询单个房间的开播状态；失败返回 null（保留上一次已知状态，不误报为未开播）。</summary>
    private async Task<bool?> TryQueryRoomLiveAsync(LiveRoom room)
    {
        var provider = _platforms.Providers.FirstOrDefault(candidate => candidate.Platform == room.Platform);
        if (provider is null)
        {
            return null;
        }

        try
        {
            var cookie = await _credentials.GetCookieAsync(room.Platform).ConfigureAwait(true);
            using var cts = new CancellationTokenSource(RoomStatusCheckTimeout);
            var status = await provider.GetRoomStatusAsync(room.RoomId, cookie, cts.Token).ConfigureAwait(true);
            return status.IsLive;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[room-status] 查询房间开播状态失败 {room.Platform}/{room.RoomId}：{ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
