using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// Step 6 主窗口：左侧主库、方案工具栏、Dock 工作区。
/// 所有仓储调用都走 AppServices，ViewModel 不直接接触 SQLite。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly WorkspacePlayerManager _players;
    private readonly WorkspaceDockBuilder _dockBuilder = new();
    private readonly DockSerializer _serializer = new();

    /// <summary>恢复比例后触发，让视图把模型 Proportion 重新同步到可见的 ContentPresenter。</summary>
    public event EventHandler? ProportionsRestored;
    private readonly Dictionary<Guid, LiveRoom> _roomById = new();

    private LayoutProfile? _selectedProfile;
    private LayoutProfile? _loadedProfile;
    private IRootDock? _layout;
    private string _searchText = string.Empty;
    private string _statusText = "就绪";
    private bool _isDirty;
    private bool _suppressProfileLoad;
    private bool _initialized;
    private bool _disposed;
    private int _gridRows = 2;
    private int _gridColumns = 2;
    private bool _isCustomLayout;
    private string? _customLayoutSnapshotJson;
    private List<LayoutSlot>? _customLayoutSnapshotSlots;
    private bool _customLayoutSnapshotDirty;
    private bool _canRestoreCustomLayout;
    private bool _suppressLayoutTracking;
    private bool _layoutTrackingArmed;
    private IRootDock? _trackedLayout;
    private double _workspaceAspect = 16.0 / 9.0;
    // 按窗格实例记录，不能用 PaneId：重建布局会复用 pane-1/pane-2，旧 ID 会挡住新实例的事件订阅。
    private readonly HashSet<RoomTileViewModel> _wiredTiles = new(ReferenceEqualityComparer.Instance);
    // 主库在线状态缓存：null=未知，true=直播中，false=未开播/失败。
    private readonly ConcurrentDictionary<Guid, bool?> _knownLiveStatus = new();
    private RoomTileViewModel? _focusedTile;
    private MpvPlayerView? _focusPlayer;
    private Dictionary<string, bool>? _focusMuteSnapshot;
    private readonly SystemMetricsService _metrics = new();
    private Dictionary<string, bool>? _masterMuteSnapshot;
    private bool _isMasterMuted;
    private string _groupFilter = "全部";
    private DispatcherTimer? _healthTimer;
    private readonly Dictionary<string, long> _lastDropCounts = new(StringComparer.Ordinal);
    private string _healthText = "解码正常 (0% 丢帧)";
    private bool _isHealthWarning;
    private DispatcherTimer? _focusHintTimer;
    private bool _isFocusHintVisible;
    private bool _isHighlightPanelVisible;

    /// <summary>V0.3：true 表示顶栏切到「录制中心」视图；监控工作区仍保留在视觉树中。</summary>
    [ObservableProperty]
    private bool _isRecordingCenterVisible;

    private int _unicycleBlockedUserCount;
    private const int MaxHighlightEvents = 200;

    // V0.2 弹幕：订阅所有权放在 ViewModel（不挂视觉树），每个窗格一条引用计数句柄。
    // 同一 (平台, 房间) 由 DanmakuManager 去重，焦点浮层不额外订阅。
    private readonly Dictionary<RoomTileViewModel, DanmakuSubscription> _danmakuHandles =
        new(ReferenceEqualityComparer.Instance);
    private bool _danmakuMasterEnabled = true;
    private bool _danmakuStickersEnabled = true;
    private bool _danmakuEmoteImagesEnabled;
    private bool _danmakuReady;

    private sealed record DanmakuSubscription(DanmakuSessionHandle Handle, PlatformType Platform, string RoomId);

    private sealed record TilePlaybackSnapshot(RoomStatus? Status, string StateText, string DetailText);

    private sealed record TileAudioSnapshot(bool Muted, int Volume);

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        _players = new WorkspacePlayerManager(services);
    }

    public ObservableCollection<RoomListItemViewModel> Rooms { get; } = new();

    public ObservableCollection<RoomListItemViewModel> FilteredRooms { get; } = new();

    /// <summary>侧栏分组 pills，第一项固定为「全部」，其余按现有房间的分组动态生成。</summary>
    public ObservableCollection<string> Groups { get; } = new();

    public ObservableCollection<LayoutProfile> Profiles { get; } = new();

    public LayoutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && !_suppressProfileLoad && _initialized)
            {
                _ = SelectProfileAsync(value);
            }
        }
    }

    /// <summary>由 View 注入：脏方案切换时弹「保存并切换/放弃修改/取消」。</summary>
    public Func<Task<ProfileSwitchChoice>>? ConfirmProfileSwitchAsync { get; set; }

    /// <summary>由 View 注入：删除历史录像前弹确认窗，返回 true 表示确认删除。</summary>
    public Func<RecordingHistoryItemViewModel, Task<bool>>? ConfirmRecordingDeleteAsync { get; set; }

    /// <summary>由 View 注入：选择录像保存目录，返回绝对路径；null 表示用户取消。</summary>
    public Func<Task<string?>>? PickRecordingDirectoryAsync { get; set; }

    /// <summary>V0.3 录制中心 ViewModel（懒创建，视图与窗口生命周期内常驻）。</summary>
    public RecordingCenterViewModel RecordingCenter => _recordingCenter ??= CreateRecordingCenter();

    private RecordingCenterViewModel? _recordingCenter;

    private RecordingCenterViewModel CreateRecordingCenter()
    {
        var center = new RecordingCenterViewModel(
            _services.Rooms,
            _services.RecordingTargets,
            _services.RecordingSessions,
            _services.Settings,
            _services.Recordings);

        center.ConfirmDeleteAsync = item =>
            ConfirmRecordingDeleteAsync?.Invoke(item) ?? Task.FromResult(false);
        center.PickDirectoryAsync = () =>
            PickRecordingDirectoryAsync?.Invoke() ?? Task.FromResult<string?>(null);
        center.OpenClipStudioAsync = OpenClipStudioAsync;
        return center;
    }

    /// <summary>V0.4：true 表示顶栏切到「剪辑台」视图；监控工作区与录制中心保留在视觉树中。</summary>
    [ObservableProperty]
    private bool _isClipStudioVisible;

    /// <summary>V0.4 剪辑台 ViewModel（懒创建：首次进入剪辑台时才构造播放器/导出面板）。</summary>
    public ClipStudioViewModel ClipStudio => _clipStudio ??= CreateClipStudio();

    private ClipStudioViewModel? _clipStudio;

    /// <summary>由 View 注入：剪辑台选择本地视频文件，返回绝对路径；null 表示取消。</summary>
    public Func<Task<string?>>? PickClipVideoFileAsync { get; set; }

    /// <summary>由 View 注入：剪辑台选择导出目录，返回绝对路径；null 表示取消。</summary>
    public Func<Task<string?>>? PickClipExportDirectoryAsync { get; set; }

    private ClipStudioViewModel CreateClipStudio()
    {
        var studio = new ClipStudioViewModel(
            _services.RecordingSessions,
            _services.HighlightEvents,
            _services.Settings,
            _services.VideoProbe,
            _services.Waveforms,
            _services.Encoders,
            _services.Clips);
        studio.PickVideoFileAsync = () =>
            PickClipVideoFileAsync?.Invoke() ?? Task.FromResult<string?>(null);
        studio.PickExportDirectoryAsync = () =>
            PickClipExportDirectoryAsync?.Invoke() ?? Task.FromResult<string?>(null);
        return studio;
    }

    public IRootDock? Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

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

    public string HealthText
    {
        get => _healthText;
        private set => SetProperty(ref _healthText, value);
    }

    public bool IsHealthWarning
    {
        get => _isHealthWarning;
        private set => SetProperty(ref _isHealthWarning, value);
    }

    /// <summary>V0.2：由 DanmakuManager 推送的累计被拦截用户数。</summary>
    public string UnicycleBlockedText => $"独轮车已拦: {_unicycleBlockedUserCount} 人";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public int GridRows
    {
        get => _gridRows;
        private set => SetProperty(ref _gridRows, value);
    }

    public int GridColumns
    {
        get => _gridColumns;
        private set => SetProperty(ref _gridColumns, value);
    }

    public bool IsCustomLayout
    {
        get => _isCustomLayout;
        private set
        {
            if (SetProperty(ref _isCustomLayout, value))
            {
                OnPropertyChanged(nameof(LayoutModeText));
            }
        }
    }

    public string LayoutModeText => IsCustomLayout ? "自定义比例" : "自动适配";

    /// <summary>点过一键自适应后，是否还能恢复上一次的自定义比例。</summary>
    public bool CanRestoreCustomLayout
    {
        get => _canRestoreCustomLayout;
        private set => SetProperty(ref _canRestoreCustomLayout, value);
    }

    public bool IsFocused => _focusedTile is not null;

    public bool IsFocusHintVisible
    {
        get => _isFocusHintVisible;
        private set => SetProperty(ref _isFocusHintVisible, value);
    }

    public SystemMetricsService Metrics => _metrics;

    public bool IsMasterMuted
    {
        get => _isMasterMuted;
        private set
        {
            if (SetProperty(ref _isMasterMuted, value))
            {
                OnPropertyChanged(nameof(MasterMuteIcon));
                OnPropertyChanged(nameof(MasterMuteToolTip));
            }
        }
    }

    public string MasterMuteIcon => IsMasterMuted ? "\uE74F" : "\uE767";

    public string MasterMuteToolTip => IsMasterMuted ? "取消全部静音" : "全部静音";

    /// <summary>全屏浮层专用播放器；与窗格里的播放器互不复用，退出时释放。</summary>
    public MpvPlayerView? FocusPlayer
    {
        get => _focusPlayer;
        private set => SetProperty(ref _focusPlayer, value);
    }

    public string FocusedTitle => _focusedTile?.DisplayTitle ?? string.Empty;

    public string RoomCountText => $"主库 {Rooms.Count} 路 · 工作区 {GridRows}×{GridColumns}";

    /// <summary>高能事件面板是否展开；只影响主窗口叠加层，不写入方案 / Dock 布局。</summary>
    public bool IsHighlightPanelVisible
    {
        get => _isHighlightPanelVisible;
        set => SetProperty(ref _isHighlightPanelVisible, value);
    }

    /// <summary>高能事件流，最新的在最前面；内存里最多保留 200 条，完整历史在数据库。</summary>
    public ObservableCollection<HighlightEventItem> HighlightEvents { get; } = new();

    public bool IsHighlightEventsEmpty => HighlightEvents.Count == 0;

    /// <summary>更新状态栏「独轮车已拦」用户数（UI 线程或后台线程均可调用）。</summary>
    public void SetUnicycleBlockedUserCount(int count)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetUnicycleBlockedUserCount(count));
            return;
        }

        var clamped = Math.Max(0, count);
        if (clamped == _unicycleBlockedUserCount)
        {
            return;
        }

        _unicycleBlockedUserCount = clamped;
        OnPropertyChanged(nameof(UnicycleBlockedText));
    }

    /// <summary>收一条高能事件并插入面板顶部（后台线程安全）。</summary>
    public void AddHighlightEvent(HighlightEvent highlightEvent)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddHighlightEvent(highlightEvent));
            return;
        }

        var item = new HighlightEventItem(highlightEvent, ResolveRoomTitle(highlightEvent.RoomId));
        HighlightEvents.Insert(0, item);
        while (HighlightEvents.Count > MaxHighlightEvents)
        {
            HighlightEvents.RemoveAt(HighlightEvents.Count - 1);
        }

        OnPropertyChanged(nameof(IsHighlightEventsEmpty));
    }

    /// <summary>启动时从数据库回填最近的高能事件（调用方保证按时间倒序）。</summary>
    public void LoadHighlightEvents(IEnumerable<HighlightEvent> highlightEvents)
    {
        HighlightEvents.Clear();
        foreach (var highlightEvent in highlightEvents)
        {
            HighlightEvents.Add(new HighlightEventItem(highlightEvent, ResolveRoomTitle(highlightEvent.RoomId)));
        }

        OnPropertyChanged(nameof(IsHighlightEventsEmpty));
    }

    public void ClearHighlightEvents()
    {
        HighlightEvents.Clear();
        OnPropertyChanged(nameof(IsHighlightEventsEmpty));
    }

    private string? ResolveRoomTitle(string roomId) =>
        _roomById.Values.FirstOrDefault(room =>
            string.Equals(room.RoomId, roomId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(room.Alias))?.Alias;

    /// <summary>工作区实际宽高比，用于 Auto-Fit 计算最接近 16:9 的行列数。</summary>
    public void SetWorkspaceAspect(double width, double height)
    {
        if (width > 1 && height > 1)
        {
            _workspaceAspect = width / height;
        }
    }

    /// <summary>用户在工作区按下鼠标后，才把后续 Proportion 变化视为“拖拽分割线”。</summary>
    public void BeginCustomLayoutTracking()
    {
        _layoutTrackingArmed = true;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        await ReloadRoomsAsync(ct).ConfigureAwait(true);

        var profiles = await _services.Profiles.GetAllAsync(ct).ConfigureAwait(true);
        if (profiles.Count == 0)
        {
            var layout = _dockBuilder.CreateGrid(2, 2, paneId => new RoomTileViewModel(paneId, null));
            var profile = new LayoutProfile { Name = "默认方案" };
            profile.DockLayoutJson = SerializeLayout(layout);
            await _services.Profiles.SaveAsync(profile, ct).ConfigureAwait(true);
            profiles = await _services.Profiles.GetAllAsync(ct).ConfigureAwait(true);
        }

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        _initialized = true;

        // 先初始化弹幕管理器，再加载方案：LoadProfileAsync 结束时会 SyncDanmakuSubscriptions，
        // 若管理器尚未 Initialize，Acquire 会抛 "请先 InitializeAsync"（启动报错窗口的根因）。
        await InitializeDanmakuAsync(ct).ConfigureAwait(true);

        _suppressProfileLoad = true;
        SelectedProfile = Profiles[0];
        _suppressProfileLoad = false;
        await LoadProfileAsync(SelectedProfile).ConfigureAwait(true);
        StartHealthMonitor();
        _loadedProfile = SelectedProfile;
    }

    /// <summary>
    /// 初始化弹幕管理器：加载屏蔽词/高能参数，回填历史高能事件，并按当前工作区建立订阅。
    /// </summary>
    private async Task InitializeDanmakuAsync(CancellationToken ct)
    {
        await _services.Danmaku.InitializeAsync(ct).ConfigureAwait(true);
        _danmakuReady = true;

        // V0.3：录制引擎在弹幕初始化之后启动；失败只记录状态，不阻断主界面启动。
        try
        {
            await _services.Recordings.InitializeAsync(ct).ConfigureAwait(true);

            // 录制中心必须在初始化时订阅录制引擎事件并首次刷新；否则录制中的
            // 时长/码率/大小等实时指标不会更新（历史录像也只在打开视图时刷新）。
            await RecordingCenter.InitializeAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus($"录制引擎初始化失败：{ex.Message}");
        }

        _services.Danmaku.HighlightTriggered += OnDanmakuHighlightTriggered;
        _services.Danmaku.UnicycleBlockedUserCountChanged += SetUnicycleBlockedUserCount;

        var recent = await _services.Danmaku
            .GetRecentHighlightEventsAsync(MaxHighlightEvents, ct)
            .ConfigureAwait(true);
        LoadHighlightEvents(recent);
        await ApplyDanmakuMasterSettingAsync(ct).ConfigureAwait(true);
        SyncDanmakuSubscriptions();
    }

    /// <summary>顶栏切换：回到监控工作区；Dock、播放器与后台录制均保持运行。</summary>
    [RelayCommand]
    private void SwitchToWorkspace()
    {
        IsRecordingCenterVisible = false;
        IsClipStudioVisible = false;
        _clipStudio?.SuspendPlayback();
    }

    /// <summary>顶栏切换：进入录制中心并刷新直播间/历史录像列表。</summary>
    [RelayCommand]
    private async Task SwitchToRecordingCenterAsync()
    {
        IsClipStudioVisible = false;
        _clipStudio?.SuspendPlayback();
        IsRecordingCenterVisible = true;
        try
        {
            // 幂等：启动阶段通常已完成订阅；这里兜底，避免初始化失败后永远收不到实时进度。
            await RecordingCenter.InitializeAsync().ConfigureAwait(true);
            await RecordingCenter.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus($"刷新录制中心失败：{ex.Message}");
        }
    }

    /// <summary>顶栏切换：进入剪辑台；首次进入时初始化并刷新录像列表。</summary>
    [RelayCommand]
    private async Task SwitchToClipStudioAsync()
    {
        IsRecordingCenterVisible = false;
        IsClipStudioVisible = true;
        try
        {
            var studio = ClipStudio;
            await studio.InitializeAsync().ConfigureAwait(true);
            await studio.RefreshSessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus($"初始化剪辑台失败：{ex.Message}");
        }
    }

    /// <summary>V0.4：录制中心「去剪辑」入口，加载指定录像场次并切到剪辑台。</summary>
    public async Task OpenClipStudioAsync(RecordingSession session)
    {
        IsRecordingCenterVisible = false;
        IsClipStudioVisible = true;
        try
        {
            var studio = ClipStudio;
            await studio.InitializeAsync().ConfigureAwait(true);
            await studio.RefreshSessionsAsync().ConfigureAwait(true);
            await studio.OpenSessionAsync(session).ConfigureAwait(true);
            SetStatus($"已进入剪辑台：{session.RoomDisplayName}");
        }
        catch (Exception ex)
        {
            SetStatus($"打开剪辑台失败：{ex.Message}");
        }
    }

    private void OnDanmakuHighlightTriggered(HighlightEvent highlightEvent) => AddHighlightEvent(highlightEvent);

    /// <summary>读取设置页的全局弹幕渲染总闸并立即应用（连接与高能统计不受影响）。</summary>
    public async Task ApplyDanmakuMasterSettingAsync(CancellationToken ct = default)
    {
        var raw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuPreviewEnabled, ct)
            .ConfigureAwait(true);
        _danmakuMasterEnabled = string.IsNullOrWhiteSpace(raw)
            || !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        var stickerRaw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuStickerVisible, ct)
            .ConfigureAwait(true);
        _danmakuStickersEnabled = string.IsNullOrWhiteSpace(stickerRaw)
            || !string.Equals(stickerRaw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        // 表情图片默认开启（用户反馈"[文本]看不出来"）：只有设置页显式保存 false 才关闭；只渲染静态帧。
        var emoteImagesRaw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuEmoteImagesVisible, ct)
            .ConfigureAwait(true);
        _danmakuEmoteImagesEnabled = string.IsNullOrWhiteSpace(emoteImagesRaw)
            || !string.Equals(emoteImagesRaw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        ApplyDanmakuMasterEnabled();
    }

    private void ApplyDanmakuMasterEnabled()
    {
        if (Layout is not null)
        {
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout))
            {
                if (tile.PlayerView is { } player)
                {
                    player.DanmakuMasterEnabled = _danmakuMasterEnabled;
                    player.DanmakuStickersEnabled = _danmakuStickersEnabled;
                    player.DanmakuEmoteImagesEnabled = _danmakuEmoteImagesEnabled;
                }
            }
        }

        if (_focusPlayer is { } focusPlayer)
        {
            focusPlayer.DanmakuMasterEnabled = _danmakuMasterEnabled;
            focusPlayer.DanmakuStickersEnabled = _danmakuStickersEnabled;
            focusPlayer.DanmakuEmoteImagesEnabled = _danmakuEmoteImagesEnabled;
        }
    }

    /// <summary>
    /// 让弹幕连接与工作区中的房间保持一致。订阅只跟「房间是否在工作区」绑定：
    /// 关弹幕（窗格开关/全局总闸）只停渲染，连接与高能统计继续。
    /// </summary>
    private void SyncDanmakuSubscriptions()
    {
        // _danmakuReady：DanmakuManager 尚未 InitializeAsync 时不能 Acquire（会抛
        // "请先 InitializeAsync"）。启动时先初始化管理器再加载方案，这里做双保险。
        if (!_initialized || !_danmakuReady || _disposed || Layout is null)
        {
            return;
        }

        ApplyDanmakuMasterEnabled();

        var activeTiles = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom && !string.IsNullOrWhiteSpace(tile.RoomId))
            .ToList();
        var activeSet = new HashSet<RoomTileViewModel>(activeTiles, ReferenceEqualityComparer.Instance);

        foreach (var pair in _danmakuHandles.ToList())
        {
            if (activeSet.Contains(pair.Key)
                && pair.Value.Platform == pair.Key.Platform
                && string.Equals(pair.Value.RoomId, pair.Key.RoomId, StringComparison.Ordinal))
            {
                continue;
            }

            pair.Value.Handle.Dispose();
            _danmakuHandles.Remove(pair.Key);
        }

        foreach (var tile in activeTiles)
        {
            if (_danmakuHandles.ContainsKey(tile))
            {
                continue;
            }

            DanmakuSessionHandle handle;
            try
            {
                handle = _services.Danmaku.Acquire(tile.Platform, tile.RoomId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 管理器未初始化/已释放时不让整个方案加载失败：跳过该窗格，等待下次 Sync 重试。
                StatusText = $"弹幕订阅失败：{ex.Message}";
                continue;
            }

            var capturedTile = tile;
            handle.MessagesReceived += messages => OnDanmakuMessagesReceived(capturedTile, messages);
            _danmakuHandles[tile] = new DanmakuSubscription(handle, tile.Platform, tile.RoomId);
        }
    }

    private void OnDanmakuMessagesReceived(RoomTileViewModel tile, IReadOnlyList<DanmakuMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            DeliverDanmakuBatch(tile, messages);
        }
        else
        {
            Dispatcher.UIThread.Post(() => DeliverDanmakuBatch(tile, messages));
        }
    }

    private void DeliverDanmakuBatch(RoomTileViewModel tile, IReadOnlyList<DanmakuMessage> messages)
    {
        if (_disposed || !_danmakuHandles.ContainsKey(tile))
        {
            return;
        }

        tile.PlayerView?.PushDanmaku(messages);
        if (ReferenceEquals(tile, _focusedTile))
        {
            _focusPlayer?.PushDanmaku(messages);
        }
    }

    private void ReleaseDanmakuSubscriptions()
    {
        foreach (var subscription in _danmakuHandles.Values)
        {
            subscription.Handle.Dispose();
        }

        _danmakuHandles.Clear();
    }

    public async Task<RoomListItemViewModel> AddRoomAsync(string url, string? groupTag = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("请粘贴直播间链接或房间号。", nameof(url));
        }

        var provider = _services.Platforms.GetProvider(url.Trim());
        var parsedRoomId = provider.ExtractRoomId(url.Trim());
        var cookie = await _services.Credentials.GetCookieAsync(provider.Platform, ct).ConfigureAwait(true);
        var status = await provider.GetRoomStatusAsync(parsedRoomId, cookie, ct).ConfigureAwait(true);
        var canonicalRoomId = string.IsNullOrWhiteSpace(status.RoomId) ? parsedRoomId : status.RoomId;

        if (await _services.Rooms.ExistsAsync(provider.Platform, canonicalRoomId, ct).ConfigureAwait(true))
        {
            throw new InvalidOperationException("这个直播间已经在主库中，不能重复添加。");
        }

        var room = new LiveRoom
        {
            DisplayNumber = await _services.Rooms.GetNextDisplayNumberAsync(ct).ConfigureAwait(true),
            Platform = provider.Platform,
            RoomId = canonicalRoomId,
            RawUrl = url.Trim(),
            Alias = string.IsNullOrWhiteSpace(status.AnchorName)
                ? $"{provider.Platform}_{canonicalRoomId}"
                : status.AnchorName,
            GroupTag = string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _knownLiveStatus[room.Id] = status.IsLive;
        await _services.Rooms.AddAsync(room, ct).ConfigureAwait(true);
        await ReloadRoomsAsync(ct).ConfigureAwait(true);
        StatusText = $"已添加 {room.Alias}";

        var item = Rooms.FirstOrDefault(candidate => candidate.Id == room.Id);
        return item ?? new RoomListItemViewModel(room);
    }

    public async Task AddRoomToWorkspaceAsync(Guid roomId, CancellationToken ct = default, bool startPlayback = true)
    {
        if (Layout is null)
        {
            throw new InvalidOperationException("工作区尚未初始化。");
        }

        var tiles = WorkspaceDockBuilder.EnumerateTiles(Layout).ToList();
        var existing = tiles.FirstOrDefault(tile => tile.LiveRoomId == roomId);
        if (existing is not null)
        {
            StatusText = $"{existing.DisplayTitle} 已在工作区";
            return;
        }

        if (!_roomById.TryGetValue(roomId, out var room))
        {
            room = await _services.Rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true);
            if (room is null)
            {
                throw new InvalidOperationException("房间不存在。");
            }

            _roomById[room.Id] = room;
        }

        // 自动适配模式：按“已有房间 + 新房间”的数量重算最优网格。
        if (!IsCustomLayout)
        {
            var rooms = tiles
                .Where(tile => tile.HasRoom)
                .Select(tile => _roomById.GetValueOrDefault(tile.LiveRoomId))
                .Where(roomItem => roomItem is not null)
                .Select(roomItem => roomItem!)
                .ToList();
            rooms.Add(room);
            await FitRoomsAsync(rooms, $"已添加 {room.Alias}", ct, startPlayback).ConfigureAwait(true);
            SyncDanmakuSubscriptions();
            return;
        }

        // 自定义比例模式：不重排全局，只填入空窗格；没有空窗格时拆分最后一个窗格。
        var empty = tiles.FirstOrDefault(tile => tile.IsPlaceholder);
        if (empty is not null)
        {
            empty.Assign(room);
            IsDirty = true;
            StatusText = $"已放入 {empty.DisplayTitle}";
            if (startPlayback)
            {
                await _players.StartAsync(empty, ct).ConfigureAwait(true);
            }

            SyncDanmakuSubscriptions();
            return;
        }

        await AppendPaneAsync(room, tiles, ct, startPlayback).ConfigureAwait(true);
        SyncDanmakuSubscriptions();
    }

    /// <summary>一键自适应：按当前活跃房间数重排，并清除“自定义比例”标记。</summary>
    public async Task AutoFitAsync(CancellationToken ct = default)
    {
        if (Layout is null)
        {
            return;
        }

        CaptureCustomLayoutSnapshot();

        var rooms = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom)
            .Select(tile => _roomById.GetValueOrDefault(tile.LiveRoomId))
            .Where(room => room is not null)
            .Select(room => room!)
            .ToList();

        // 行列数不变时只把现有分割线归位，不销毁播放器：
        // 这样音频的静音/音量、当前画面和房间在线状态都不会被重置。
        var current = WorkspaceDockBuilder.GetGridDimensions(Layout);
        var (desiredRows, desiredColumns) = WorkspaceDockBuilder.CalculateAutoFitGrid(
            Math.Max(1, rooms.Count), _workspaceAspect);
        var allTiles = WorkspaceDockBuilder.EnumerateTiles(Layout).ToList();
        var currentRoomCount = allTiles.Count(tile => tile.HasRoom);
        // 有空格时必须重建：否则会把空窗格留在网格里，3 路无法收成“上 1 下 2”。
        if (rooms.Count == currentRoomCount &&
            allTiles.Count == rooms.Count &&
            current.Rows == desiredRows &&
            current.Columns == desiredColumns &&
            WorkspaceDockBuilder.ResetProportions(Layout))
        {
            SetCustomLayout(false);
            IsDirty = true;
            StatusText = rooms.Count == 0 ? "已重置为自动适配" : $"已自适应 {rooms.Count} 路（比例已归位）";
            ProportionsRestored?.Invoke(this, EventArgs.Empty);
            return;
        }

        await FitRoomsAsync(rooms, rooms.Count == 0 ? "已重置为自动适配" : $"已自适应 {rooms.Count} 路", ct)
            .ConfigureAwait(true);
    }

    /// <summary>把当前自定义比例（结构 + 每格房间）暂存，供一键自适应后恢复。</summary>
    private void CaptureCustomLayoutSnapshot()
    {
        if (!IsCustomLayout || Layout is null)
        {
            return;
        }

        _customLayoutSnapshotJson = SerializeLayout(Layout);
        _customLayoutSnapshotSlots = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom)
            .Select(tile => new LayoutSlot
            {
                LayoutProfileId = Guid.Empty,
                LiveRoomId = tile.LiveRoomId,
                PaneId = tile.PaneId,
                DefaultMuted = true,
                DefaultDanmakuVisible = tile.IsDanmakuVisible,
                IsFocusSlot = false,
                PreferredQualityId = tile.Status?.AvailableQualities.FirstOrDefault()?.Id
            })
            .ToList();
        _customLayoutSnapshotDirty = IsDirty;
        CanRestoreCustomLayout = true;
    }

    /// <summary>恢复被「一键自适应」覆盖前的自定义比例；不写入方案库。</summary>
    public async Task RestoreCustomLayoutAsync(CancellationToken ct = default)
    {
        if (_customLayoutSnapshotJson is null || _customLayoutSnapshotSlots is null)
        {
            return;
        }

        // 同一批 PaneId 的方案切换会走 LoadProfileAsync 的原地复用分支，
        // 该分支不替换 Dock 树，因此不会应用快照里的 Proportion。
        // 恢复比例时直接把快照比例套回现有树，播放器完全不动。
        if (Layout is not null)
        {
            IRootDock? snapshot = null;
            try
            {
                snapshot = _serializer.Deserialize<IRootDock>(_customLayoutSnapshotJson);
            }
            catch (Exception ex)
            {
                StatusText = $"自定义比例快照解析失败，将重建布局：{ex.Message}";
            }

            if (snapshot is not null && WorkspaceDockBuilder.TryApplyProportions(Layout, snapshot))
            {
                SetCustomLayout(true);
                IsDirty = _customLayoutSnapshotDirty;
                ClearCustomLayoutSnapshot();
                StatusText = "已恢复自定义比例";
                ProportionsRestored?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        // 结构不一致（或快照损坏）时才走完整重建，避免把比例套到错误的窗格上。
        var profile = new LayoutProfile
        {
            Name = "自定义比例（恢复）",
            DockLayoutJson = _customLayoutSnapshotJson,
            IsCustomLayout = true
        };
        foreach (var slot in _customLayoutSnapshotSlots)
        {
            profile.Slots.Add(new LayoutSlot
            {
                LayoutProfileId = profile.Id,
                LiveRoomId = slot.LiveRoomId,
                PaneId = slot.PaneId,
                DefaultMuted = slot.DefaultMuted,
                IsFocusSlot = slot.IsFocusSlot,
                PreferredQualityId = slot.PreferredQualityId
            });
        }

        await LoadProfileAsync(profile).ConfigureAwait(true);
        SetCustomLayout(true);
        IsDirty = _customLayoutSnapshotDirty;
        ClearCustomLayoutSnapshot();
        StatusText = "已恢复自定义比例（播放器已重建）";
        ProportionsRestored?.Invoke(this, EventArgs.Empty);
    }

    private void ClearCustomLayoutSnapshot()
    {
        _customLayoutSnapshotJson = null;
        _customLayoutSnapshotSlots = null;
        _customLayoutSnapshotDirty = false;
        CanRestoreCustomLayout = false;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (Layout is null)
        {
            return;
        }

        var dimensions = WorkspaceDockBuilder.GetGridDimensions(Layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        var profile = SelectedProfile ?? new LayoutProfile { Name = "默认方案" };
        profile.IsCustomLayout = IsCustomLayout;
        profile.DockLayoutJson = SerializeLayout(Layout);
        profile.Slots.Clear();

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.HasRoom))
        {
            profile.Slots.Add(new LayoutSlot
            {
                LayoutProfileId = profile.Id,
                LiveRoomId = tile.LiveRoomId,
                PaneId = tile.PaneId,
                DefaultMuted = true,
                DefaultDanmakuVisible = tile.IsDanmakuVisible,
                IsFocusSlot = false,
                PreferredQualityId = tile.Status?.AvailableQualities.FirstOrDefault()?.Id
            });
        }

        await _services.Profiles.SaveAsync(profile, ct).ConfigureAwait(true);
        if (!Profiles.Contains(profile))
        {
            Profiles.Add(profile);
        }

        _suppressProfileLoad = true;
        SelectedProfile = profile;
        _loadedProfile = profile;
        _suppressProfileLoad = false;
        IsDirty = false;
        StatusText = $"已保存方案「{profile.Name}」";
    }

    public async Task SaveAsAsync(string name, CancellationToken ct = default)
    {
        if (Layout is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("方案名不能为空。", nameof(name));
        }

        var profile = new LayoutProfile { Name = name.Trim(), IsCustomLayout = IsCustomLayout };
        profile.DockLayoutJson = SerializeLayout(Layout);

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.HasRoom))
        {
            profile.Slots.Add(new LayoutSlot
            {
                LayoutProfileId = profile.Id,
                LiveRoomId = tile.LiveRoomId,
                PaneId = tile.PaneId,
                DefaultMuted = true,
                DefaultDanmakuVisible = tile.IsDanmakuVisible,
                PreferredQualityId = tile.Status?.AvailableQualities.FirstOrDefault()?.Id
            });
        }

        await _services.Profiles.SaveAsync(profile, ct).ConfigureAwait(true);
        Profiles.Add(profile);
        _suppressProfileLoad = true;
        SelectedProfile = profile;
        _loadedProfile = profile;
        _suppressProfileLoad = false;
        IsDirty = false;
        StatusText = $"已另存方案「{profile.Name}」";
    }

    public void MarkDirty() => IsDirty = true;

    public void SetStatus(string message) => StatusText = message;

    /// <summary>订阅窗格的双击全屏/移出工作区请求；每个窗格只订阅一次。</summary>
    private void WireTileInteractions(IRootDock? layout)
    {
        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(layout))
        {
            if (!_wiredTiles.Add(tile))
            {
                continue;
            }

            tile.FocusRequested += OnTileFocusRequested;
            tile.RemoveRequested += OnTileRemoveRequested;
            tile.SwapRequested += OnTileSwapRequested;
        }
    }

    private void OnTileSwapRequested(object? sender, RoomTileViewModel target)
    {
        if (sender is RoomTileViewModel source)
        {
            _ = SwapPanesAsync(source, target);
        }
    }

    /// <summary>拖拽两个窗格互换房间：保留窗格位置，交换房间内容并重建两路播放器。</summary>
    private async Task SwapPanesAsync(RoomTileViewModel source, RoomTileViewModel target)
    {
        if (ReferenceEquals(source, target) || source.IsPlaceholder || target.IsPlaceholder)
        {
            return;
        }

        if (!_roomById.TryGetValue(source.LiveRoomId, out var sourceRoom) ||
            !_roomById.TryGetValue(target.LiveRoomId, out var targetRoom))
        {
            return;
        }

        DisposePanePlayer(source);
        DisposePanePlayer(target);

        source.ClearRoom();
        target.ClearRoom();
        source.Assign(targetRoom);
        target.Assign(sourceRoom);

        await Task.WhenAll(
            _players.StartAsync(source),
            _players.StartAsync(target)).ConfigureAwait(true);

        IsDirty = true;
        StatusText = $"已交换 {source.DisplayTitle} 与 {target.DisplayTitle}";
        SyncDanmakuSubscriptions();
    }

    private void DisposePanePlayer(RoomTileViewModel tile)
    {
        _players.Stop(tile.PaneId);
        tile.PlayerView?.Dispose();
        tile.PlayerView = null;
    }

    private void OnTileFocusRequested(object? sender, EventArgs e)
    {
        if (sender is RoomTileViewModel tile)
        {
            _ = ToggleFocusAsync(tile);
        }
    }

    private void OnTileRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is RoomTileViewModel tile)
        {
            _ = RemoveRoomFromWorkspaceAsync(tile);
        }
    }

    /// <summary>双击同一窗格在「全屏」和普通平铺之间切换。</summary>
    public void ToggleFocus(RoomTileViewModel tile) => _ = ToggleFocusAsync(tile);

    /// <summary>
    /// 全屏用独立 MpvPlayerView 浮层播放，避免移动原窗格的 OpenGL 控件。
    /// 原窗格播放器全部静音，只保留浮层声音。
    /// </summary>
    public async Task ToggleFocusAsync(RoomTileViewModel tile)
    {
        if (Layout is null || tile.IsPlaceholder || tile.Status is null)
        {
            return;
        }

        if (ReferenceEquals(_focusedTile, tile))
        {
            ExitFocus();
            return;
        }

        ExitFocus();

        var quality = QualityPolicy.Choose(tile.Status, 1080, 1, true)
                      ?? tile.Status.AvailableQualities.FirstOrDefault();
        if (quality is null)
        {
            StatusText = "全屏失败：当前房间没有可用画质";
            return;
        }

        _focusMuteSnapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var candidate in WorkspaceDockBuilder.EnumerateTiles(Layout))
        {
            var existing = candidate.PlayerView;
            if (existing is null)
            {
                continue;
            }

            _focusMuteSnapshot[candidate.PaneId] = existing.IsMuted;
            existing.SetMute(true);
        }

        _focusedTile = tile;
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(FocusedTitle));
        ShowFocusHint();

        // DataContext 指向原窗格 tile：弹幕开关、标题等绑定在浮层里同样生效。
        var player = new MpvPlayerView { RoomTitle = tile.DisplayTitle, DataContext = tile };
        player.DanmakuMasterEnabled = _danmakuMasterEnabled;
        player.DanmakuStickersEnabled = _danmakuStickersEnabled;
        player.DanmakuEmoteImagesEnabled = _danmakuEmoteImagesEnabled;
        if (IsMasterMuted)
        {
            player.SetMute(true);
        }

        FocusPlayer = player;
        StatusText = $"全屏：{tile.DisplayTitle}（Esc 退出）";

        try
        {
            // 让浮层先完成挂载/GL 初始化，再 loadfile。
            await Task.Delay(150);
            await player.PlayAsync(quality);
        }
        catch (Exception ex)
        {
            StatusText = $"全屏播放失败：{ex.Message}";
        }
    }

    public void ExitFocus()
    {
        if (_focusedTile is null)
        {
            return;
        }

        var player = FocusPlayer;
        FocusPlayer = null;
        player?.Dispose();

        if (_focusMuteSnapshot is not null && Layout is not null)
        {
            foreach (var candidate in WorkspaceDockBuilder.EnumerateTiles(Layout))
            {
                if (_focusMuteSnapshot.TryGetValue(candidate.PaneId, out var muted))
                {
                    candidate.PlayerView?.SetMute(muted);
                }
            }
        }

        _focusMuteSnapshot = null;
        _focusedTile = null;
        HideFocusHint();
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(FocusedTitle));
        StatusText = "已退出全屏";
    }

    private void ShowFocusHint()
    {
        IsFocusHintVisible = true;
        _focusHintTimer?.Stop();
        _focusHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _focusHintTimer.Tick += (_, _) =>
        {
            _focusHintTimer?.Stop();
            IsFocusHintVisible = false;
        };
        _focusHintTimer.Start();
    }

    private void HideFocusHint()
    {
        _focusHintTimer?.Stop();
        _focusHintTimer = null;
        IsFocusHintVisible = false;
    }

    /// <summary>顶部全部静音：首次记录每路静音状态，恢复时按原状态还原，而不是全部开声。</summary>
    public void ToggleMasterMute()
    {
        if (Layout is null && FocusPlayer is null)
        {
            return;
        }

        if (!IsMasterMuted)
        {
            _masterMuteSnapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout))
            {
                var player = tile.PlayerView;
                if (player is null)
                {
                    continue;
                }

                _masterMuteSnapshot[tile.PaneId] = player.IsMuted;
                player.SetMute(true);
            }

            if (FocusPlayer is { } focusPlayer)
            {
                _masterMuteSnapshot["focus"] = focusPlayer.IsMuted;
                focusPlayer.SetMute(true);
            }

            IsMasterMuted = true;
            StatusText = "已全部静音";
            return;
        }

        if (_masterMuteSnapshot is not null)
        {
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout))
            {
                if (_masterMuteSnapshot.TryGetValue(tile.PaneId, out var muted))
                {
                    tile.PlayerView?.SetMute(muted);
                }
            }

            if (FocusPlayer is { } focusPlayer && _masterMuteSnapshot.TryGetValue("focus", out var focusMuted))
            {
                focusPlayer.SetMute(focusMuted);
            }
        }

        _masterMuteSnapshot = null;
        IsMasterMuted = false;
        StatusText = "已恢复全部静音前的状态";
    }

    /// <summary>把房间移出工作区：停止播放并清空窗格，主库记录保留。</summary>
    public async Task UpdateRoomAsync(
        Guid roomId,
        string alias,
        string? groupTag,
        int displayNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("显示名称不能为空。", nameof(alias));
        }

        if (displayNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayNumber), "序号必须大于 0。");
        }

        var room = await _services.Rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true)
                   ?? throw new InvalidOperationException("直播间不存在。");
        room.Alias = alias.Trim();
        room.GroupTag = string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim();
        room.DisplayNumber = displayNumber;
        await _services.Rooms.UpdateAsync(room, ct).ConfigureAwait(true);

        if (Layout is not null)
        {
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.LiveRoomId == roomId))
            {
                tile.UpdateMetadata(room);
                if (tile.PlayerView is { } player)
                {
                    player.RoomTitle = tile.DisplayTitle;
                }
            }
        }

        await ReloadRoomsAsync(ct).ConfigureAwait(true);
        StatusText = $"已更新 {room.Alias}";
    }

    public async Task DeleteRoomAsync(Guid roomId, CancellationToken ct = default)
    {
        var room = await _services.Rooms.GetByIdAsync(roomId, ct).ConfigureAwait(true);
        if (room is null)
        {
            return;
        }

        // 删除直播间前先停止该房间的录制：历史录像保留（LiveRoomId 由外键置空），
        // 但不允许在直播间被删除后仍有一个正在写入的录制会话。
        if (_services.Recordings.IsRecording(roomId))
        {
            try
            {
                await _services.Recordings.StopAsync(roomId, ct).ConfigureAwait(true);
                SetStatus($"已停止 {room.Alias} 的录制。");
            }
            catch (Exception ex)
            {
                SetStatus($"停止录制失败，已取消删除：{ex.Message}");
                return;
            }
        }

        if (Layout is not null)
        {
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.LiveRoomId == roomId).ToList())
            {
                if (_focusedTile == tile)
                {
                    ExitFocus();
                }

                _players.Stop(tile.PaneId);
                if (tile.PlayerView is { } player)
                {
                    tile.PlayerView = null;
                    player.Dispose();
                }

                tile.ClearRoom();
            }
        }

        await _services.Rooms.DeleteAsync(roomId, ct).ConfigureAwait(true);
        _roomById.Remove(roomId);
        _knownLiveStatus.TryRemove(roomId, out _);
        await ReloadRoomsAsync(ct).ConfigureAwait(true);
        await ReflowAutoAsync(ct).ConfigureAwait(true);
        SyncDanmakuSubscriptions();
        MarkDirty();
        StatusText = $"已从主库删除 {room.Alias}";
    }

    public async Task RemoveRoomFromWorkspaceAsync(RoomTileViewModel tile)
    {
        if (_focusedTile == tile)
        {
            ExitFocus();
        }

        var title = tile.DisplayTitle;
        _players.Stop(tile.PaneId);
        if (tile.PlayerView is { } player)
        {
            tile.PlayerView = null;
            player.Dispose();
        }

        tile.ClearRoom();
        await ReflowAutoAsync(CancellationToken.None).ConfigureAwait(true);
        SyncDanmakuSubscriptions();
        IsDirty = true;
        StatusText = $"已移出 {title}";
        OnPropertyChanged(nameof(RoomCountText));
    }

    /// <summary>自动模式下，房间被删除/移出后立即按剩余房间重排；自定义比例保持原结构。</summary>
    private async Task ReflowAutoAsync(CancellationToken ct)
    {
        if (IsCustomLayout || Layout is null)
        {
            return;
        }

        var rooms = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom)
            .Select(tile => _roomById.GetValueOrDefault(tile.LiveRoomId))
            .Where(room => room is not null)
            .Select(room => room!)
            .ToList();

        await FitRoomsAsync(rooms, rooms.Count == 0 ? "已重置为自动适配" : $"已自动适配 {rooms.Count} 路", ct)
            .ConfigureAwait(true);
    }

    /// <summary>重排前保存每个房间的播放/音频状态，重排后恢复，避免统一静音和“未开播”闪烁。</summary>
    private (Dictionary<Guid, TilePlaybackSnapshot> Playback, Dictionary<Guid, TileAudioSnapshot> Audio)
        CaptureRoomStates(IRootDock? layout)
    {
        var playback = new Dictionary<Guid, TilePlaybackSnapshot>();
        var audio = new Dictionary<Guid, TileAudioSnapshot>();
        if (layout is null)
        {
            return (playback, audio);
        }

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(layout).Where(tile => tile.HasRoom))
        {
            playback[tile.LiveRoomId] = new TilePlaybackSnapshot(tile.Status, tile.StateText, tile.DetailText);
            if (tile.PlayerView is { } player)
            {
                audio[tile.LiveRoomId] = new TileAudioSnapshot(player.IsMuted, player.Volume);
            }
        }

        return (playback, audio);
    }

    private RoomTileViewModel CreateRebuiltTile(
        string paneId,
        LiveRoom? room,
        IReadOnlyDictionary<Guid, TilePlaybackSnapshot> playback)
    {
        var tile = new RoomTileViewModel(paneId, room);
        if (room is not null && playback.TryGetValue(room.Id, out var snapshot))
        {
            // 先恢复旧状态文案/画质信息，起播请求发出前不会闪成“未开播”。
            tile.Status = snapshot.Status;
            tile.StateText = snapshot.StateText;
            tile.DetailText = snapshot.DetailText;
        }

        return tile;
    }

    private async Task StartRebuiltTilesAsync(
        IRootDock layout,
        IReadOnlyDictionary<Guid, TileAudioSnapshot> audio,
        CancellationToken ct)
    {
        var starts = new List<Task>();
        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(layout).Where(tile => tile.HasRoom))
        {
            var player = _players.EnsurePlayer(tile);
            if (audio.TryGetValue(tile.LiveRoomId, out var state))
            {
                player.SetVolume(state.Volume);
                player.SetMute(state.Muted);
            }

            player.ShowLoading("正在重新连接…");
            starts.Add(_players.StartAsync(tile, ct));
        }

        await Task.WhenAll(starts).ConfigureAwait(true);
        SyncDanmakuSubscriptions();
    }

    private async Task FitRoomsAsync(IReadOnlyList<LiveRoom> rooms, string status, CancellationToken ct, bool startPlayback = true)
    {
        ExitFocus();
        var (playbackStates, audioStates) = CaptureRoomStates(Layout);
        DisposePlayers(Layout);
        DetachLayoutTracking();

        var index = 0;
        var layout = _dockBuilder.CreateAutoFit(rooms.Count, _workspaceAspect, paneId =>
        {
            var room = index < rooms.Count ? rooms[index++] : null;
            return CreateRebuiltTile(paneId, room, playbackStates);
        });

        _suppressLayoutTracking = true;
        _dockBuilder.InitLayout(layout);
        Layout = layout;
        var dimensions = WorkspaceDockBuilder.GetGridDimensions(layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        SetCustomLayout(false);
        IsDirty = true;
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        OnPropertyChanged(nameof(RoomCountText));
        StatusText = status;

        await Task.Yield();
        if (!startPlayback)
        {
            return;
        }

        await StartRebuiltTilesAsync(layout, audioStates, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 自定义比例下工作区已满：复制现有树并把新窗格追加到最后一个横排行，
    /// 不重排用户已有的 Proportion。
    /// </summary>
    private async Task AppendPaneAsync(LiveRoom room, IReadOnlyList<RoomTileViewModel> existingTiles, CancellationToken ct, bool startPlayback = true)
    {
        if (Layout is null)
        {
            return;
        }

        var nextNumber = existingTiles
            .Select(tile => tile.PaneId)
            .Where(id => id.StartsWith("pane-", StringComparison.Ordinal))
            .Select(id => int.TryParse(id.AsSpan(5), out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var extra = new RoomTileViewModel($"pane-{nextNumber}", room);
        var (playbackStates, audioStates) = CaptureRoomStates(Layout);

        RoomTileViewModel Factory(string paneId)
        {
            if (string.Equals(paneId, extra.PaneId, StringComparison.Ordinal))
            {
                return extra;
            }

            var old = existingTiles.FirstOrDefault(tile => tile.PaneId == paneId);
            _roomById.TryGetValue(old?.LiveRoomId ?? Guid.Empty, out var existingRoom);
            return CreateRebuiltTile(paneId, existingRoom, playbackStates);
        }

        ExitFocus();
        DisposePlayers(Layout);
        DetachLayoutTracking();
        var layout = _dockBuilder.CloneStructure(Layout, Factory, extra);
        _suppressLayoutTracking = true;
        _dockBuilder.InitLayout(layout);
        WorkspaceDockBuilder.BindTiles(layout, Factory);
        Layout = layout;
        var dimensions = WorkspaceDockBuilder.GetGridDimensions(layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        IsDirty = true;
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        OnPropertyChanged(nameof(RoomCountText));
        StatusText = $"已拆分新窗格并放入 {room.Alias}";

        await Task.Yield();
        if (!startPlayback)
        {
            return;
        }

        await StartRebuiltTilesAsync(layout, audioStates, ct).ConfigureAwait(true);
    }

    private void SetCustomLayout(bool value)
    {
        if (_isCustomLayout == value)
        {
            return;
        }

        IsCustomLayout = value;
        OnPropertyChanged(nameof(LayoutModeText));
    }

    private void AttachLayoutTracking(IRootDock layout)
    {
        DetachLayoutTracking();
        _layoutTrackingArmed = false;
        _trackedLayout = layout;
        foreach (var dockable in EnumerateDockables(layout))
        {
            if (dockable is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged += OnLayoutPropertyChanged;
            }
        }
    }

    private void DetachLayoutTracking()
    {
        if (_trackedLayout is null)
        {
            return;
        }

        foreach (var dockable in EnumerateDockables(_trackedLayout))
        {
            if (dockable is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged -= OnLayoutPropertyChanged;
            }
        }

        _trackedLayout = null;
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressLayoutTracking ||
            !_layoutTrackingArmed ||
            e.PropertyName != nameof(IDockable.Proportion) ||
            IsCustomLayout)
        {
            return;
        }

        SetCustomLayout(true);
        IsDirty = true;
        StatusText = "已切换为自定义比例；新增房间只追加，不会重排";
    }

    private static IEnumerable<IDockable> EnumerateDockables(IDockable root)
    {
        yield return root;
        if (root is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                foreach (var nested in EnumerateDockables(child))
                {
                    yield return nested;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _healthTimer?.Stop();
        _healthTimer = null;
        _focusHintTimer?.Stop();
        _focusHintTimer = null;
        ReleaseDanmakuSubscriptions();
        ExitFocus();
        DetachLayoutTracking();
        DisposePlayers(Layout);
        _players.Dispose();
        _metrics.Dispose();

        try
        {
            _recordingCenter?.Dispose();
            _recordingCenter = null;
        }
        catch
        {
            // 录制中心释放失败不阻塞窗口关闭。
        }

        try
        {
            // V0.4：剪辑台导出任务与其 Job Object 句柄一并释放（会级联终止残留 ffmpeg）。
            _clipStudio?.Dispose();
            _clipStudio = null;
            _services.DisposeClipExportResources();
        }
        catch
        {
            // 剪辑台释放失败不阻塞窗口关闭。
        }

        try
        {
            // 录制引擎退出前停止所有录制并 flush 弹幕文件；最多等 5 秒，失败不阻塞关闭。
            Task.Run(async () => await _services.Recordings.DisposeAsync().ConfigureAwait(false))
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 退出时录制收尾失败不阻塞关闭。
        }

        try
        {
            // 弹幕管理器退出前 flush 高能事件落盘；最多等 2 秒，失败不阻塞窗口关闭。
            Task.Run(async () => await _services.Danmaku.DisposeAsync().ConfigureAwait(false))
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 退出时落盘失败不阻塞关闭。
        }
    }

    private async Task SelectProfileAsync(LayoutProfile? profile)
    {
        if (profile is null || ReferenceEquals(profile, _loadedProfile))
        {
            return;
        }

        if (IsDirty && !_suppressProfileLoad)
        {
            var choice = ConfirmProfileSwitchAsync is null
                ? ProfileSwitchChoice.Cancel
                : await ConfirmProfileSwitchAsync().ConfigureAwait(true);

            if (choice == ProfileSwitchChoice.Cancel)
            {
                _suppressProfileLoad = true;
                SetProperty(ref _selectedProfile, _loadedProfile, nameof(SelectedProfile));
                _suppressProfileLoad = false;
                return;
            }

            if (choice == ProfileSwitchChoice.Save)
            {
                await SaveAsync().ConfigureAwait(true);
            }
        }

        await LoadProfileAsync(profile).ConfigureAwait(true);
        _loadedProfile = profile;
        ClearCustomLayoutSnapshot();
    }

    private static bool HasSamePaneIds(IRootDock left, IRootDock right)
    {
        var leftIds = WorkspaceDockBuilder.EnumerateTiles(left)
            .Select(tile => tile.PaneId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var rightIds = WorkspaceDockBuilder.EnumerateTiles(right)
            .Select(tile => tile.PaneId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return leftIds.SequenceEqual(rightIds);
    }

    private async Task LoadProfileAsync(LayoutProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        ExitFocus();
        var previousLayout = Layout;
        var previousTiles = WorkspaceDockBuilder.EnumerateTiles(previousLayout)
            .Where(tile => tile.HasRoom)
            .ToList();
        var previousByRoom = previousTiles
            .GroupBy(tile => tile.LiveRoomId)
            .ToDictionary(group => group.Key, group => group.First());
        var previousRoomIds = previousByRoom.Keys.ToList();

        IRootDock? layout = null;

        if (!string.IsNullOrWhiteSpace(profile.DockLayoutJson))
        {
            try
            {
                layout = _serializer.Deserialize<IRootDock>(profile.DockLayoutJson);
            }
            catch (Exception ex)
            {
                StatusText = $"方案「{profile.Name}」反序列化失败，已重建：{ex.Message}";
            }
        }

        if (layout is null)
        {
            // 旧方案没有 DockLayoutJson 时，必须和“一键自适应”使用同一套行分布；
            // 不能退回 sqrt 方格，否则 3 路会变成 2×2 + 一个空窗格。
            var count = Math.Max(1, profile.Slots.Count);
            layout = _dockBuilder.CreateAutoFit(count, _workspaceAspect, paneId =>
            {
                var slot = profile.Slots.FirstOrDefault(candidate => candidate.PaneId == paneId);
                _roomById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
                return new RoomTileViewModel(paneId, room)
                {
                    IsDanmakuVisible = slot?.DefaultDanmakuVisible ?? false
                };
            });
        }
        else
        {
            // 旧方案 JSON 没有 splitter 节点，加载时修复，避免必须点一次「一键自适应」才能拖拽。
            _dockBuilder.EnsureSplitters(layout);
            // 旧 JSON 可能把 CanDrag/CanDrop 序列化成 false，加载后统一恢复。
            WorkspaceDockBuilder.EnableDragging(layout);
        }

        try
        {
            _dockBuilder.InitLayout(layout);
        }
        catch (InvalidOperationException)
        {
            // A deserialized layout may already be initialized.
        }

        // DockLayoutJson 只保存结构；运行时窗格数据按 PaneId 从 LayoutSlots 重建。
        // 必须在 InitLayout 之后绑定，否则 InitLayout 会重置 ActiveDockable。
        WorkspaceDockBuilder.BindTiles(layout, paneId =>
        {
            var slot = profile.Slots.FirstOrDefault(candidate => candidate.PaneId == paneId);
            _roomById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
            return new RoomTileViewModel(paneId, room)
            {
                IsDanmakuVisible = slot?.DefaultDanmakuVisible ?? false
            };
        });

        var newTiles = WorkspaceDockBuilder.EnumerateTiles(layout)
            .Where(tile => tile.HasRoom)
            .ToList();

        // 两个方案的窗格集合一致时，不替换 Dock 树，直接复用原窗格和 PlayerView。
        // 这是切换方案最常见的情况，也是零黑屏的关键：控件不移动，底层 MpvContext 不断流。
        if (previousLayout is not null && HasSamePaneIds(previousLayout, layout))
        {
            var slotsByPane = profile.Slots.ToDictionary(slot => slot.PaneId);
            foreach (var oldTile in WorkspaceDockBuilder.EnumerateTiles(previousLayout))
            {
                slotsByPane.TryGetValue(oldTile.PaneId, out var slot);
                _roomById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
                oldTile.IsDanmakuVisible = slot?.DefaultDanmakuVisible ?? false;
                if (oldTile.LiveRoomId == (room?.Id ?? Guid.Empty))
                {
                    continue;
                }

                _players.Stop(oldTile.PaneId);
                oldTile.PlayerView?.Dispose();
                oldTile.PlayerView = null;

                if (room is null)
                {
                    oldTile.ClearRoom();
                    continue;
                }

                oldTile.Assign(room);
                _ = _players.StartAsync(oldTile);
            }

            _loadedProfile = profile;
            SetCustomLayout(profile.IsCustomLayout);
            WireTileInteractions(previousLayout);
            var oldDimensions = WorkspaceDockBuilder.GetGridDimensions(previousLayout);
            GridRows = oldDimensions.Rows;
            GridColumns = oldDimensions.Columns;
            IsDirty = false;
            StatusText = $"已载入方案「{profile.Name}」：窗格结构一致，播放器原地保留。";
            SyncDanmakuSubscriptions();
            return;
        }

        var newRoomIds = newTiles.Select(tile => tile.LiveRoomId).ToList();
        var diff = LayoutDiffService.Plan(previousRoomIds, newRoomIds);

        // 窗格结构变化时无法安全搬运 OpenGL 控件（会触发 MPV_ERROR_VO_INIT_FAILED），
        // 只能完整重建；结构一致的常见切换已在上面的原地复用分支返回。
        var rebuilt = previousLayout is not null;
        if (rebuilt)
        {
            DisposePlayers(previousLayout);
        }

        var transferred = 0;

        _suppressLayoutTracking = true;
        Layout = layout;
        var dimensions = WorkspaceDockBuilder.GetGridDimensions(layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        SetCustomLayout(profile.IsCustomLayout);
        IsDirty = false;
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        OnPropertyChanged(nameof(RoomCountText));

        // 差集只销毁被移除的房间；交集播放器已转移，不重新拉流。
        foreach (var roomId in diff.Removed)
        {
            if (!previousByRoom.TryGetValue(roomId, out var oldTile))
            {
                continue;
            }

            _players.Stop(oldTile.PaneId);
            oldTile.PlayerView?.Dispose();
            oldTile.PlayerView = null;
        }

        StatusText = rebuilt
            ? $"已载入方案「{profile.Name}」：布局结构变化，已重建 {newTiles.Count} 路"
            : diff.HasChanges
                ? $"已载入方案「{profile.Name}」：复用 {transferred} 路，销毁 {diff.Removed.Count} 路，新增 {diff.Added.Count} 路"
                : $"已载入方案「{profile.Name}」";

        await Task.Yield();
        foreach (var tile in newTiles.Where(tile => tile.PlayerView is null))
        {
            await _players.StartAsync(tile).ConfigureAwait(true);
        }

        SyncDanmakuSubscriptions();
    }

    private async Task ReloadRoomsAsync(CancellationToken ct)
    {
        var rooms = await _services.Rooms.GetAllAsync(ct).ConfigureAwait(true);
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
        OnPropertyChanged(nameof(RoomCountText));
    }

    private void RefreshFilteredRooms()
    {
        var query = SearchText.Trim();
        FilteredRooms.Clear();

        foreach (var room in Rooms)
        {
            var groupMatches = GroupFilter == "全部" ||
                               string.Equals(room.GroupTag, GroupFilter, StringComparison.OrdinalIgnoreCase);
            if (!groupMatches)
            {
                continue;
            }

            if (query.Length == 0 ||
                room.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredRooms.Add(room);
            }
        }
    }

    private void RebuildGroups()
    {
        var groups = Rooms
            .Select(room => room.GroupTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Groups.Clear();
        Groups.Add("全部");
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        if (!Groups.Any(group => string.Equals(group, GroupFilter, StringComparison.OrdinalIgnoreCase)))
        {
            GroupFilter = "全部";
        }
    }

    private void StartHealthMonitor()
    {
        if (_healthTimer is not null)
        {
            return;
        }

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) => RefreshHealth();
        _healthTimer.Start();
    }

    private void RefreshHealth()
    {
        if (Layout is null)
        {
            return;
        }

        long dropped = 0;
        double frames = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout))
        {
            var player = tile.PlayerView;
            if (player is null)
            {
                continue;
            }

            var metrics = player.GetHealthMetrics();
            var current = metrics.DecoderDroppedFrames + metrics.DroppedFrames;
            if (!_lastDropCounts.TryGetValue(tile.PaneId, out var previous))
            {
                _lastDropCounts[tile.PaneId] = current;
                seen.Add(tile.PaneId);
                continue;
            }

            dropped += Math.Max(0, current - previous);
            frames += Math.Max(1, metrics.FramesPerSecond) * 2;
            _lastDropCounts[tile.PaneId] = current;
            seen.Add(tile.PaneId);
        }

        foreach (var stale in _lastDropCounts.Keys.Except(seen).ToList())
        {
            _lastDropCounts.Remove(stale);
        }

        var percent = frames > 0 ? dropped / frames * 100 : 0;
        IsHealthWarning = percent >= 5;
        HealthText = IsHealthWarning
            ? $"解码告警 ({percent:F1}% 丢帧)"
            : $"解码正常 ({percent:F0}% 丢帧)";

        SyncRoomStatuses();
    }

    /// <summary>把工作区已知的直播状态同步到左侧主库；从未检测过的房间保持灰色未知。</summary>
    private void SyncRoomStatuses()
    {
        if (Layout is null)
        {
            return;
        }

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.HasRoom))
        {
            bool? live = tile.StateText switch
            {
                "未开播" => false,
                "播放失败" => false,
                _ => tile.Status?.IsLive
            };

            if (live is not null)
            {
                _knownLiveStatus[tile.LiveRoomId] = live;
            }
        }

        foreach (var item in Rooms)
        {
            if (_knownLiveStatus.TryGetValue(item.Id, out var live))
            {
                item.SetLiveStatus(live);
            }
        }
    }

    private string SerializeLayout(IRootDock layout)
    {
        // 直接序列化活布局会把 Tool.Context 里的 Avalonia 控件（PlayerView）带进去，
        // 形成 MdiBounds 循环。这里复制结构并清空 Context，保留自定义的 Proportion 与拆分行列。
        var clean = _dockBuilder.CloneStructure(layout, paneId => new RoomTileViewModel(paneId, null));
        return _serializer.Serialize(clean);
    }

    private void DisposePlayers(IRootDock? layout)
    {
        if (layout is null)
        {
            return;
        }

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(layout))
        {
            _players.Stop(tile.PaneId);
            tile.PlayerView?.Dispose();
            tile.PlayerView = null;
        }
    }
}
