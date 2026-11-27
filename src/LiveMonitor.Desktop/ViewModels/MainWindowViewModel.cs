using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Platforms.CustomStream;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// Step 6 主窗口：左侧主库、方案工具栏、Dock 工作区。
/// 所有仓储调用都走 AppServices，ViewModel 不直接接触 SQLite。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable, IRoomCatalogHost, IWorkspaceLayoutProfileHost
{
    private readonly AppServices _services;
    private readonly WorkspacePlayerManager _players;
    private readonly WorkspaceDockBuilder _dockBuilder = new();
    private readonly DockSerializer _serializer = new();

    /// <summary>恢复比例后触发，让视图把模型 Proportion 重新同步到可见的 ContentPresenter。</summary>
    public event EventHandler? ProportionsRestored;

    private IRootDock? _layout;
    private string _toastText = string.Empty;
    private bool _isToastVisible;
    private CancellationTokenSource? _toastCts;
    private bool _initialized;
    private bool _disposed;
    private int _gridRows = 2;
    private int _gridColumns = 2;
    private bool _isCustomLayout;
    private bool _isHeroLayout;
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
    private RoomTileViewModel? _focusedTile;
    private MpvPlayerView? _focusPlayer;
    private Dictionary<string, bool>? _focusMuteSnapshot;
    private Dictionary<string, bool>? _masterMuteSnapshot;
    private bool _isMasterMuted;
    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _focusHintTimer;
    private bool _isFocusHintVisible;

    // V0.6：侧栏主播开播状态独立轮询。工作区只覆盖当前打开的房间，
    // 未放入工作区的房间也要能显示直播/未开播，不能一直停在「状态未知」。
    // 计时器（Avalonia DispatcherTimer）留在窗口层，查询/缓存/应用由 RoomCatalog 子 VM 负责。
    private DispatcherTimer? _roomStatusTimer;
    private static readonly TimeSpan RoomStatusPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>V0.3：true 表示顶栏切到「录制中心」视图；监控工作区仍保留在视觉树中。</summary>
    [ObservableProperty]
    private bool _isRecordingCenterVisible;

    /// <summary>底部状态栏只读子视图模型；主 VM 在指标 / 健康 / 工作区计数更新处写入，并向窗口转发变更。</summary>
    public SystemStatusBarViewModel SystemStatus { get; } = new();

    /// <summary>左侧直播间主库子视图模型；主 VM 把房间集合/筛选/增删交给它，并转发属性变更。</summary>
    public RoomCatalogViewModel RoomCatalog { get; }

    /// <summary>顶部布局方案子视图模型；主 VM 把 Profiles/保存/切换交给它，并转发属性变更。</summary>
    public WorkspaceLayoutProfileViewModel LayoutProfiles { get; }

    /// <summary>右侧高能事件面板只读子视图模型；主 VM 在事件入库 / 回填 / 清空处写入。</summary>
    public HighlightFeedViewModel HighlightFeed { get; } = new();

    // V0.2 弹幕：订阅所有权放在 ViewModel（不挂视觉树），每个窗格一条引用计数句柄。
    // 同一 (平台, 房间) 由 DanmakuManager 去重，焦点浮层不额外订阅。
    private readonly Dictionary<RoomTileViewModel, DanmakuSubscription> _danmakuHandles =
        new(ReferenceEqualityComparer.Instance);
    private bool _danmakuMasterEnabled = true;
    private bool _danmakuStickersEnabled = true;
    private bool _danmakuEmoteImagesEnabled = true;
    private bool _danmakuReady;

    private sealed record DanmakuSubscription(DanmakuSessionHandle Handle, PlatformType Platform, string RoomId);

    private sealed record TilePlaybackSnapshot(RoomStatus? Status, string StateText, string DetailText);

    private sealed record TileAudioSnapshot(bool Muted, int Volume);

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        _players = new WorkspacePlayerManager(services);
        RoomCatalog = new RoomCatalogViewModel(services.Rooms, services.Platforms, services.Credentials, this);
        LayoutProfiles = new WorkspaceLayoutProfileViewModel(services.Profiles, CreateDefaultProfileLayoutJson, this);

        // 四个子视图模型对外只读；主 VM 订阅其变更并转发为窗口级 PropertyChanged，
        // 使既有绑定、代码后置与测试无需改动。转发在子 VM 的原始线程（UI 线程）同步完成。
        SystemStatus.PropertyChanged += OnSystemStatusPropertyChanged;
        HighlightFeed.PropertyChanged += OnHighlightFeedPropertyChanged;
        RoomCatalog.PropertyChanged += OnRoomCatalogPropertyChanged;
        LayoutProfiles.PropertyChanged += OnLayoutProfilesPropertyChanged;
        SystemStatus.UpdateWorkspaceSummary(
            RoomCatalog.Rooms.Count,
            IsHeroLayout,
            WorkspaceLayoutCoordinator.CountOccupiedPanes(Layout),
            GridRows,
            GridColumns);
    }

    private void OnSystemStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 状态栏子 VM 的属性名与主 VM 转发属性同名，直接按名转发。
        OnPropertyChanged(e.PropertyName);
    }

    private void OnHighlightFeedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HighlightFeedViewModel.IsEmpty):
                OnPropertyChanged(nameof(IsHighlightEventsEmpty));
                break;
            case nameof(HighlightFeedViewModel.IsVisible):
                OnPropertyChanged(nameof(IsHighlightPanelVisible));
                break;
        }
    }

    private void OnRoomCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 主库子 VM 的属性名与主 VM 转发属性同名（SearchText/GroupFilter），直接按名转发，
        // 让代码后置的分组胶囊刷新与既有绑定继续生效。
        OnPropertyChanged(e.PropertyName);
    }

    private void OnLayoutProfilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 方案子 VM 的 SelectedProfile / IsDirty 由主 VM 同名转发，顶栏脏点与方案框继续绑定主 VM。
        OnPropertyChanged(e.PropertyName);
    }

    // 主库目录 / 布局方案两个子 VM 对外只暴露稳定集合实例，主 VM 只做属性转发，
    // 既有绑定路径、代码后置与测试保持原样。
    public ObservableCollection<RoomListItemViewModel> Rooms => RoomCatalog.Rooms;

    public ObservableCollection<RoomListItemViewModel> FilteredRooms => RoomCatalog.FilteredRooms;

    /// <summary>侧栏分组 pills，第一项固定为「全部」，其余按现有房间的分组动态生成。</summary>
    public ObservableCollection<string> Groups => RoomCatalog.Groups;

    public ObservableCollection<LayoutProfile> Profiles => LayoutProfiles.Profiles;

    public LayoutProfile? SelectedProfile
    {
        get => LayoutProfiles.SelectedProfile;
        set => LayoutProfiles.SelectedProfile = value;
    }

    /// <summary>由 View 注入：脏方案切换时弹「保存并切换/放弃修改/取消」。</summary>
    public Func<Task<ProfileSwitchChoice>>? ConfirmProfileSwitchAsync
    {
        get => LayoutProfiles.ConfirmProfileSwitchAsync;
        set => LayoutProfiles.ConfirmProfileSwitchAsync = value;
    }

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
        center.TryGetLiveStatus = RoomCatalog.TryGetLiveStatus;
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
            _services.Clips,
            _services.Recordings,
            _services.LiveWaveforms,
            _services.Asr);
        studio.PickVideoFileAsync = () =>
            PickClipVideoFileAsync?.Invoke() ?? Task.FromResult<string?>(null);
        studio.PickExportDirectoryAsync = () =>
            PickClipExportDirectoryAsync?.Invoke() ?? Task.FromResult<string?>(null);
        return studio;
    }

    public IRootDock? Layout
    {
        get => _layout;
        private set
        {
            if (SetProperty(ref _layout, value))
            {
                WorkspaceTilesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string SearchText
    {
        get => RoomCatalog.SearchText;
        set => RoomCatalog.SearchText = value;
    }

    public string GroupFilter
    {
        get => RoomCatalog.GroupFilter;
        set => RoomCatalog.GroupFilter = value;
    }

    /// <summary>解码健康摘要文案（顶栏健康胶囊与状态栏共用 SystemStatus 的数据）。</summary>
    public string HealthText => SystemStatus.HealthText;

    public bool IsHealthWarning => SystemStatus.IsHealthWarning;

    /// <summary>V0.2：由 DanmakuManager 推送的累计被拦截用户数。</summary>
    public string UnicycleBlockedText => SystemStatus.UnicycleBlockedText;

    public string StatusText
    {
        get => SystemStatus.StatusText;
        private set
        {
            if (SystemStatus.UpdateStatusText(value))
            {
                ShowToast(value);
            }
        }
    }

    /// <summary>右下角非阻塞 Toast 文案，随 StatusText 一起更新（第 4 轮：替代打断式状态提示）。</summary>
    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    /// <summary>显示 2.5 秒后自动消失的右下角 Toast；新消息会顶掉旧的。</summary>
    private void ShowToast(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastText = message.Trim();
        IsToastVisible = true;

        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = HideToastAfterDelayAsync(cts);
    }

    private async Task HideToastAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || cts.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_toastCts, cts))
            {
                IsToastVisible = false;
            }
        });
    }

    public bool IsDirty => LayoutProfiles.IsDirty;

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

    public bool IsHeroLayout
    {
        get => _isHeroLayout;
        private set
        {
            if (SetProperty(ref _isHeroLayout, value))
            {
                OnPropertyChanged(nameof(LayoutModeText));
                NotifyWorkspaceSummaryChanged();
            }
        }
    }

    public string LayoutModeText =>
        WorkspaceLayoutCoordinator.FormatLayoutModeText(IsFreeformMode, IsHeroLayout, IsCustomLayout);

    /// <summary>点过一键自适应后，是否还能恢复上一次的自定义比例。</summary>
    public bool CanRestoreCustomLayout
    {
        get => _canRestoreCustomLayout;
        private set => SetProperty(ref _canRestoreCustomLayout, value);
    }

    // V0.7：自由画布模式。平铺 Dock 树继续作为「窗格槽位 + 焦点模式 + 录制」的底层模型，
    // 画布只负责渲染与归一化摆放；两者通过 Layout 与 FreeformLayoutDocument 同步。
    private bool _isFreeformMode;
    private FreeformLayoutDocument? _freeformDocument;

    public bool IsFreeformMode
    {
        get => _isFreeformMode;
        private set
        {
            if (SetProperty(ref _isFreeformMode, value))
            {
                OnPropertyChanged(nameof(LayoutModeText));
                OnPropertyChanged(nameof(FreeformModeButtonText));
                OnPropertyChanged(nameof(FreeformModeHint));
                OnPropertyChanged(nameof(ShowDockWorkspace));
            }
        }
    }

    /// <summary>自由布局模式下由画布承载窗格，Dock 宿主必须隐藏，避免同一播放器控件被两个宿主同时挂载。</summary>
    public bool ShowDockWorkspace => !IsWorkspaceEmpty && !IsFreeformMode;
    public string FreeformModeButtonText => MainWindowStatusText.FreeformModeLabel(IsFreeformMode);

    public string FreeformModeHint => MainWindowStatusText.FreeformModeHint(IsFreeformMode);

    public FreeformLayoutDocument? FreeformDocument => _freeformDocument;

    public IReadOnlyList<RoomTileViewModel> WorkspaceTiles =>
        WorkspaceDockBuilder.EnumerateTiles(Layout).ToList();

    /// <summary>工作区窗格集合变化（增删/方案切换/布局重建），供视图同步自由画布子项。</summary>
    public event EventHandler? WorkspaceTilesChanged;

    /// <summary>自由布局/自定义比例下窗格结构被模型层修改（例如移出房间），视图需重建宿主。</summary>
    public event EventHandler? LayoutStructureChanged;

    /// <summary>请求视图把自由布局文档应用到画布。</summary>
    public event EventHandler<FreeformLayoutDocument>? FreeformApplyRequested;

    /// <summary>手动通知窗格集合变化（模型层移出窗格后由 ViewModel 主动触发）。</summary>
    public void NotifyWorkspaceTilesChanged() => WorkspaceTilesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>自由布局切换时先停掉旧播放器并解除绑定（旧控件不能再挂到新宿主上）。</summary>
    public void DetachWorkspacePlayers()
    {
        foreach (var tile in WorkspaceTiles.Where(tile => tile.HasRoom))
        {
            _players.Stop(tile.PaneId);
            tile.PlayerView = null;
        }
    }

    /// <summary>在画布/Dock 窗格重新挂好之后创建播放器（PlayAsync 需要控件已在视觉树上渲染就绪）。</summary>
    public async Task StartWorkspacePlayersAsync(CancellationToken ct = default)
    {
        foreach (var tile in WorkspaceTiles.Where(tile => tile.HasRoom).ToList())
        {
            await _players.StartAsync(tile, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 由视图注入：把工作区宿主从自由画布切回平铺 Dock（清空画布、恢复 Dock 布局绑定、重新挂载前解绑播放器）。
    /// 宿主切换只能由视图完成，ViewModel 在执行平铺布局命令前通过此回调协调。
    /// </summary>
    public Func<Task>? LeaveFreeformHostAsync { get; set; }

    /// <summary>
    /// 由视图注入：切换方案直接进入自由布局前，先把宿主从 Dock 切到画布
    /// （保存 Dock 布局、拆树、等旧窗格视图释放、解绑播放器、清空并显示画布）。
    /// 方案加载在 ViewModel 内翻转模式，无法直接操作视图；不先切宿主会让同一个 MpvPlayerView
    /// 被 Dock 与画布两个 ContentPresenter 争用，下一次布局测量抛 already has a visual parent 并闪退。
    /// </summary>
    public Func<Task>? PrepareFreeformHostAsync { get; set; }

    /// <summary>
    /// 平铺布局命令（一键自适应/主副屏/恢复比例/切换平铺方案）执行前调用：
    /// 若当前在自由画布中，先让视图把宿主切回 Dock，再翻转模式。否则销毁仍挂在可见画布上的
    /// MpvPlayerView 会让原生渲染控件与视觉树不一致，导致闪退；而只改模式又会让新布局落在隐藏的 Dock 上，看起来没有效果。
    /// </summary>
    public async Task<bool> ExitFreeformForTiledLayoutAsync()
    {
        if (!IsFreeformMode)
        {
            return false;
        }

        if (LeaveFreeformHostAsync is { } leave)
        {
            await leave().ConfigureAwait(true);
        }

        IsFreeformMode = false;
        _freeformDocument = null;
        return true;
    }

    /// <summary>画布拖动/缩放/层级变化后由视图回写最新文档。</summary>
    public void UpdateFreeformDocument(FreeformLayoutDocument? document)
    {
        _freeformDocument = document;
        if (document is not null && IsFreeformMode)
        {
            LayoutProfiles.MarkDirty();
        }
    }

    /// <summary>切换自由画布模式。</summary>
    public void ToggleFreeformMode()
    {
        if (IsFreeformMode)
        {
            IsFreeformMode = false;
            StatusText = MainWindowStatusText.FreeformExited();
            return;
        }

        EnterFreeformMode(FreeformPreset.AutoFit);
    }

    /// <summary>进入自由布局并应用指定预设。</summary>
    public void ApplyFreeformPreset(FreeformPreset preset)
    {
        if (!IsFreeformMode)
        {
            EnterFreeformMode(preset);
            return;
        }

        var document = WorkspaceLayoutCoordinator.BuildFreeformDocument(WorkspaceTiles, preset, _workspaceAspect);
        _freeformDocument = document;
        FreeformApplyRequested?.Invoke(this, document);
        LayoutProfiles.MarkDirty();
        StatusText = MainWindowStatusText.FreeformPresetApplied(preset);
    }

    private void EnterFreeformMode(FreeformPreset preset)
    {
        if (WorkspaceTiles.Count == 0)
        {
            StatusText = MainWindowStatusText.WorkspaceEmptyHint();
            return;
        }

        IsFreeformMode = true;
        var document = WorkspaceLayoutCoordinator.BuildFreeformDocument(WorkspaceTiles, preset, _workspaceAspect);
        _freeformDocument = document;
        FreeformApplyRequested?.Invoke(this, document);
        LayoutProfiles.MarkDirty();
        StatusText = MainWindowStatusText.FreeformEntered(preset);
    }

    private string SerializeCurrentLayout(IRootDock layout) =>
        IsFreeformMode && _freeformDocument is not null
            ? _freeformDocument.Serialize()
            : SerializeLayout(layout);

    public bool IsFocused => _focusedTile is not null;

    public bool IsFocusHintVisible
    {
        get => _isFocusHintVisible;
        private set => SetProperty(ref _isFocusHintVisible, value);
    }

    /// <summary>内存 / GPU 指标（由视图在窗口打开时启动采样）。</summary>
    public SystemMetricsService Metrics => SystemStatus.Metrics;

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

    public string MasterMuteIcon => PlayerChromeLogic.MasterMuteIcon(IsMasterMuted);

    public string MasterMuteToolTip => PlayerChromeLogic.MasterMuteTip(IsMasterMuted);

    /// <summary>全屏浮层专用播放器；与窗格里的播放器互不复用，退出时释放。</summary>
    public MpvPlayerView? FocusPlayer
    {
        get => _focusPlayer;
        private set => SetProperty(ref _focusPlayer, value);
    }

    public string FocusedTitle => _focusedTile?.DisplayTitle ?? string.Empty;

    /// <summary>主库 / 工作区计数文案（数据在 SystemStatus 子 VM 中）。</summary>
    public string RoomCountText => SystemStatus.RoomCountText;

    /// <summary>工作区当前没有任何已绑定直播间的窗格（空状态引导用）。</summary>
    public bool IsWorkspaceEmpty => WorkspaceLayoutCoordinator.CountOccupiedPanes(Layout) == 0;

    /// <summary>布局/窗格变化后统一刷新状态栏文案与工作区空状态。</summary>
    private void NotifyWorkspaceSummaryChanged()
    {
        SystemStatus.UpdateWorkspaceSummary(
            Rooms.Count,
            IsHeroLayout,
            WorkspaceLayoutCoordinator.CountOccupiedPanes(Layout),
            GridRows,
            GridColumns);
        OnPropertyChanged(nameof(IsWorkspaceEmpty));
        OnPropertyChanged(nameof(ShowDockWorkspace));
    }

    /// <summary>高能事件面板是否展开；只影响主窗口叠加层，不写入方案 / Dock 布局。</summary>
    public bool IsHighlightPanelVisible
    {
        get => HighlightFeed.IsVisible;
        set => HighlightFeed.UpdateIsVisible(value);
    }

    /// <summary>高能事件流，最新的在最前面；内存里最多保留 200 条，完整历史在数据库。</summary>
    public ObservableCollection<HighlightEventItem> HighlightEvents => HighlightFeed.Items;

    public bool IsHighlightEventsEmpty => HighlightFeed.IsEmpty;

    /// <summary>更新状态栏「独轮车已拦」用户数（UI 线程或后台线程均可调用）。</summary>
    public void SetUnicycleBlockedUserCount(int count)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetUnicycleBlockedUserCount(count));
            return;
        }

        SystemStatus.UpdateUnicycleBlockedUserCount(count);
    }

    /// <summary>收一条高能事件并插入面板顶部（后台线程安全）。</summary>
    public void AddHighlightEvent(HighlightEvent highlightEvent)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddHighlightEvent(highlightEvent));
            return;
        }

        HighlightFeed.Insert(new HighlightEventItem(highlightEvent, ResolveRoomTitle(highlightEvent.RoomId)));
    }

    /// <summary>启动时从数据库回填最近的高能事件（调用方保证按时间倒序）。</summary>
    public void LoadHighlightEvents(IEnumerable<HighlightEvent> highlightEvents)
    {
        var items = highlightEvents
            .Select(highlightEvent => new HighlightEventItem(highlightEvent, ResolveRoomTitle(highlightEvent.RoomId)))
            .ToList();
        HighlightFeed.ReplaceAll(items);
    }

    public void ClearHighlightEvents() => HighlightFeed.Clear();

    private string? ResolveRoomTitle(string roomId) => RoomCatalog.ResolveRoomAlias(roomId);

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

        await RoomCatalog.ReloadRoomsAsync(ct).ConfigureAwait(true);
        await LayoutProfiles.InitializeAsync(ct).ConfigureAwait(true);

        _initialized = true;

        // 先初始化弹幕管理器，再加载方案：LoadProfileAsync 结束时会 SyncDanmakuSubscriptions，
        // 若管理器尚未 Initialize，Acquire 会抛 "请先 InitializeAsync"（启动报错窗口的根因）。
        await InitializeDanmakuAsync(ct).ConfigureAwait(true);

        await LayoutProfiles.LoadInitialProfileAsync(ct).ConfigureAwait(true);
        StartHealthMonitor();
        StartRoomStatusMonitor();
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
            SetStatus(MainWindowStatusText.RecordingEngineInitFailed(ex.Message));
        }

        _services.Danmaku.HighlightTriggered += OnDanmakuHighlightTriggered;
        _services.Danmaku.UnicycleBlockedUserCountChanged += SetUnicycleBlockedUserCount;

        var recent = await _services.Danmaku
            .GetRecentHighlightEventsAsync(HighlightFeedViewModel.MaxItems, ct)
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
            SetStatus(MainWindowStatusText.RecordingCenterRefreshFailed(ex.Message));
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
            SetStatus(MainWindowStatusText.ClipStudioInitFailed(ex.Message));
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
            SetStatus(MainWindowStatusText.ClipStudioEntered(session.RoomDisplayName));
        }
        catch (Exception ex)
        {
            SetStatus(MainWindowStatusText.ClipStudioOpenFailed(ex.Message));
        }
    }

    private void OnDanmakuHighlightTriggered(HighlightEvent highlightEvent) => AddHighlightEvent(highlightEvent);

    /// <summary>读取设置页的全局弹幕渲染总闸并立即应用（连接与高能统计不受影响）。</summary>
    public async Task ApplyDanmakuMasterSettingAsync(CancellationToken ct = default)
    {
        var raw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuPreviewEnabled, ct)
            .ConfigureAwait(true);
        _danmakuMasterEnabled = DanmakuSubscriptionLogic.IsEnabledSetting(raw);

        var stickerRaw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuStickerVisible, ct)
            .ConfigureAwait(true);
        _danmakuStickersEnabled = DanmakuSubscriptionLogic.IsEnabledSetting(stickerRaw);

        // 表情图片默认开启（用户反馈"[文本]看不出来"）：只有设置页显式保存 false 才关闭；只渲染静态帧。
        var emoteImagesRaw = await _services.Settings
            .GetAsync(AppSettingKeys.DanmakuEmoteImagesVisible, ct)
            .ConfigureAwait(true);
        _danmakuEmoteImagesEnabled = DanmakuSubscriptionLogic.IsEnabledSetting(emoteImagesRaw);

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
            .Where(tile => DanmakuSubscriptionLogic.IsSubscribeEligible(tile.HasRoom, tile.Platform, tile.RoomId))
            .ToList();
        var activeSet = new HashSet<RoomTileViewModel>(activeTiles, ReferenceEqualityComparer.Instance);

        foreach (var pair in _danmakuHandles.ToList())
        {
            if (DanmakuSubscriptionLogic.ShouldKeepSubscription(
                    activeSet.Contains(pair.Key),
                    pair.Key.Platform,
                    pair.Key.RoomId,
                    pair.Value.Platform,
                    pair.Value.RoomId))
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
                StatusText = MainWindowStatusText.DanmakuSubscribeFailed(ex.Message);
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

    public Task<RoomListItemViewModel> AddRoomAsync(string url, string? groupTag = null, CancellationToken ct = default)
    {
        return RoomCatalog.AddRoomAsync(url, groupTag, ct);
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
        return RoomCatalog.AddRoomAsync(url, groupTag, customStream, referer, userAgent, ct);
    }

    public async Task AddRoomToWorkspaceAsync(Guid roomId, CancellationToken ct = default, bool startPlayback = true)
    {
        if (Layout is null)
        {
            throw new InvalidOperationException(MainWindowStatusText.WorkspaceNotInitialized);
        }

        var tiles = WorkspaceDockBuilder.EnumerateTiles(Layout).ToList();
        var existing = tiles.FirstOrDefault(tile => tile.LiveRoomId == roomId);
        if (existing is not null)
        {
            StatusText = MainWindowStatusText.RoomAlreadyInWorkspace(existing.DisplayTitle);
            return;
        }

        var room = await RoomCatalog.GetRoomOrLoadAsync(roomId, ct).ConfigureAwait(true);
        if (room is null)
        {
            throw new InvalidOperationException(MainWindowStatusText.RoomNotFound);
        }

        // 自动适配模式：按“已有房间 + 新房间”的数量重算最优网格。
        if (!IsCustomLayout)
        {
            var rooms = tiles
                .Where(tile => tile.HasRoom)
                .Select(tile => RoomCatalog.RoomsById.GetValueOrDefault(tile.LiveRoomId))
                .Where(roomItem => roomItem is not null)
                .Select(roomItem => roomItem!)
                .ToList();
            rooms.Add(room);
            await FitRoomsAsync(rooms, MainWindowStatusText.RoomAdded(room.Alias), ct, startPlayback).ConfigureAwait(true);
            SyncDanmakuSubscriptions();
            return;
        }

        // 自定义比例模式：不重排全局，只填入空窗格；没有空窗格时拆分最后一个窗格。
        var empty = tiles.FirstOrDefault(tile => tile.IsPlaceholder);
        if (empty is not null)
        {
            empty.Assign(room);
            LayoutProfiles.MarkDirty();
            StatusText = MainWindowStatusText.RoomAddedToWorkspace(empty.DisplayTitle);
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
    public async Task AutoFitAsync(CancellationToken ct = default, bool startPlayback = true)
    {
        if (Layout is null)
        {
            return;
        }

        // 自由画布与 Dock 是两个宿主：必须先由视图把宿主切回 Dock 再重建布局，
        // 否则仍挂在画布视觉树上的播放器被销毁会闪退。
        var exitedFreeform = await ExitFreeformForTiledLayoutAsync().ConfigureAwait(true);

        CaptureCustomLayoutSnapshot();

        var rooms = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom)
            .Select(tile => RoomCatalog.RoomsById.GetValueOrDefault(tile.LiveRoomId))
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
        // 主副屏布局的行列口径与网格不同，统一走重建，避免把主副屏当成“行列未变”的网格。
        // 从自由画布退回平铺时，宿主切换会解绑每一格的播放器（tile.PlayerView = null）；
        // 此时即使行列数与目标一致也不能只归位比例，否则窗格里没有播放器、直播间不会自动加载。
        if (!exitedFreeform &&
            !IsHeroLayout &&
            rooms.Count == currentRoomCount &&
            allTiles.Count == rooms.Count &&
            current.Rows == desiredRows &&
            current.Columns == desiredColumns &&
            WorkspaceDockBuilder.ResetProportions(Layout))
        {
            SetCustomLayout(false);
            LayoutProfiles.MarkDirty();
            StatusText = rooms.Count == 0 ? MainWindowStatusText.AutoFitReset() : MainWindowStatusText.AutoFitApplied(rooms.Count);
            ProportionsRestored?.Invoke(this, EventArgs.Empty);
            return;
        }

        await FitRoomsAsync(rooms, rooms.Count == 0 ? MainWindowStatusText.AutoFitReset() : MainWindowStatusText.AutoFitAppliedShort(rooms.Count), ct, startPlayback)
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
        _customLayoutSnapshotSlots = WorkspaceLayoutCoordinator.CreateLayoutSlots(
            WorkspaceDockBuilder.EnumerateTiles(Layout),
            Guid.Empty);
        _customLayoutSnapshotDirty = LayoutProfiles.IsDirty;
        CanRestoreCustomLayout = true;
    }

    /// <summary>恢复被「一键自适应」覆盖前的自定义比例；不写入方案库。</summary>
    public async Task RestoreCustomLayoutAsync(CancellationToken ct = default)
    {
        if (_customLayoutSnapshotJson is null || _customLayoutSnapshotSlots is null)
        {
            return;
        }

        // 自由布局下恢复比例：先把宿主切回 Dock，否则比例会套到隐藏的 Dock 树上。
        var exitedFreeform = await ExitFreeformForTiledLayoutAsync().ConfigureAwait(true);

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
                StatusText = MainWindowStatusText.RatioSnapshotParseFailed(ex.Message);
            }

            if (snapshot is not null && WorkspaceDockBuilder.TryApplyProportions(Layout, snapshot))
            {
                SetCustomLayout(true);
                SetHeroLayout(WorkspaceDockBuilder.ContainsDockId(snapshot, "workspace-hero"));
                LayoutProfiles.RestoreDirty(_customLayoutSnapshotDirty);
                ClearCustomLayoutSnapshot();
                StatusText = MainWindowStatusText.RatioRestored();
                ProportionsRestored?.Invoke(this, EventArgs.Empty);
                // 从自由画布切回时宿主切换已解绑播放器，恢复比例不会重建布局，必须补一次起播。
                if (exitedFreeform)
                {
                    await StartWorkspacePlayersAsync(ct).ConfigureAwait(true);
                }

                return;
            }
        }

        // 结构不一致（或快照损坏）时才走完整重建，避免把比例套到错误的窗格上。
        var profile = new LayoutProfile
        {
            Name = MainWindowStatusText.CustomRatioRestoreProfileName,
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
        LayoutProfiles.RestoreDirty(_customLayoutSnapshotDirty);
        ClearCustomLayoutSnapshot();
        StatusText = MainWindowStatusText.RatioRestoredWithRebuild();
        ProportionsRestored?.Invoke(this, EventArgs.Empty);
    }

    private void ClearCustomLayoutSnapshot()
    {
        _customLayoutSnapshotJson = null;
        _customLayoutSnapshotSlots = null;
        _customLayoutSnapshotDirty = false;
        CanRestoreCustomLayout = false;
    }

    public Task SaveAsync(CancellationToken ct = default) => LayoutProfiles.SaveAsync(ct);

    public Task SaveAsAsync(string name, CancellationToken ct = default) => LayoutProfiles.SaveAsAsync(name, ct);

    public void MarkDirty() => LayoutProfiles.MarkDirty();

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

        if (!RoomCatalog.RoomsById.TryGetValue(source.LiveRoomId, out var sourceRoom) ||
            !RoomCatalog.RoomsById.TryGetValue(target.LiveRoomId, out var targetRoom))
        {
            return;
        }

        _players.RemovePlayer(source);
        _players.RemovePlayer(target);

        source.ClearRoom();
        target.ClearRoom();
        source.Assign(targetRoom);
        target.Assign(sourceRoom);

        await Task.WhenAll(
            _players.StartAsync(source),
            _players.StartAsync(target)).ConfigureAwait(true);

        LayoutProfiles.MarkDirty();
        StatusText = MainWindowStatusText.RoomsSwapped(source.DisplayTitle, target.DisplayTitle);
        SyncDanmakuSubscriptions();
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
            StatusText = MainWindowStatusText.FullscreenNoQuality();
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
        StatusText = MainWindowStatusText.FullscreenEntered(tile.DisplayTitle);

        try
        {
            // 让浮层先完成挂载/GL 初始化，再 loadfile。
            await Task.Delay(150);
            await player.PlayAsync(quality);
        }
        catch (Exception ex)
        {
            StatusText = MainWindowStatusText.FullscreenPlayFailed(ex.Message);
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
        StatusText = MainWindowStatusText.FullscreenExited();
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
            StatusText = MainWindowStatusText.AllMuted();
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
        StatusText = MainWindowStatusText.MuteRestored();
    }

    /// <summary>把房间移出工作区：停止播放并清空窗格，主库记录保留。</summary>
    public Task UpdateRoomAsync(
        Guid roomId,
        string alias,
        string? groupTag,
        int displayNumber,
        CancellationToken ct = default)
    {
        return UpdateRoomCoreAsync(roomId, alias, groupTag, displayNumber, rawUrl: null, referer: null, userAgent: null, ct);
    }

    /// <summary>
    /// 更新房间信息；<paramref name="rawUrl"/> 仅对自定义清流生效（替换带新 Token 的地址），
    /// 同时可更新 Referer / User-Agent 请求头并重新拉起播放以应用新地址。
    /// </summary>
    public Task UpdateRoomAsync(
        Guid roomId,
        string alias,
        string? groupTag,
        int displayNumber,
        string? rawUrl,
        string? referer,
        string? userAgent,
        CancellationToken ct = default)
    {
        return UpdateRoomCoreAsync(roomId, alias, groupTag, displayNumber, rawUrl, referer, userAgent, ct);
    }

    private async Task UpdateRoomCoreAsync(
        Guid roomId,
        string alias,
        string? groupTag,
        int displayNumber,
        string? rawUrl,
        string? referer,
        string? userAgent,
        CancellationToken ct)
    {
        var (room, streamUrlChanged) = await RoomCatalog.UpdateRoomRecordAsync(
            roomId, alias, groupTag, displayNumber, rawUrl, referer, userAgent, ct).ConfigureAwait(true);

        if (Layout is not null)
        {
            foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.LiveRoomId == roomId))
            {
                tile.UpdateMetadata(room);
                if (tile.PlayerView is { } player)
                {
                    player.RoomTitle = tile.DisplayTitle;
                }

                // 清流换成新地址后需要重新解析并拉起播放，否则会继续拉旧 URL。
                if (streamUrlChanged && tile.HasRoom && tile.PlayerView is not null)
                {
                    await _players.StartAsync(tile, ct).ConfigureAwait(true);
                }
            }
        }

        await RoomCatalog.ReloadRoomsAsync(ct).ConfigureAwait(true);

        StatusText = MainWindowStatusText.RoomUpdated(room.Alias);
    }

    public Task DeleteRoomAsync(Guid roomId, CancellationToken ct = default)
    {
        return RoomCatalog.DeleteRoomAsync(roomId, ct);
    }

    public async Task RemoveRoomFromWorkspaceAsync(RoomTileViewModel tile)
    {
        if (_focusedTile == tile)
        {
            ExitFocus();
        }

        var title = tile.DisplayTitle;

        if (IsFreeformMode || Layout is null)
        {
            _players.RemovePlayer(tile);
            tile.ClearRoom();

            // 自由布局：窗格挂在 FreeformCanvas 上，改 Dock 模型并让画布同步即可，不重建 Dock 宿主。
            var removedFromModel = WorkspaceDockBuilder.RemovePane(Layout, tile.PaneId);
            if (removedFromModel)
            {
                NotifyWorkspaceTilesChanged();
                LayoutStructureChanged?.Invoke(this, EventArgs.Empty);
            }

            if (!IsFreeformMode)
            {
                await ReflowAutoAsync(CancellationToken.None).ConfigureAwait(true);
            }

            SyncDanmakuSubscriptions();
            LayoutProfiles.MarkDirty();
            StatusText = MainWindowStatusText.RoomRemoved(title);
            NotifyWorkspaceSummaryChanged();
            return;
        }

        // Dock 宿主（自动适配 / 主副屏 / 自定义比例）：
        // 不能只 RemovePane 后原地换 Dock.Layout —— 旧 ContentPresenter 还没释放子控件，
        // 新 ContentPresenter 就再次绑定同一个 MpvPlayerView，下一次布局测量会抛
        // "already has a visual parent" 并导致白屏后闪退。这里复用自适应/主副屏的安全重建路径：
        // 先保存状态与剩余房间，销毁旧播放器并解绑跟踪，再用全新窗格模型重建整棵树。
        var keepHero = IsHeroLayout;
        var keepCustom = IsCustomLayout;
        var (playbackStates, audioStates) = CaptureRoomStates(Layout);
        var remainingTiles = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(candidate => candidate.HasRoom && !ReferenceEquals(candidate, tile))
            .ToList();
        var rooms = remainingTiles
            .Select(candidate => RoomCatalog.RoomsById.GetValueOrDefault(candidate.LiveRoomId))
            .Where(room => room is not null)
            .Select(room => room!)
            .ToList();

        ExitFocus();
        DisposePlayers(Layout);
        DetachLayoutTracking();
        tile.ClearRoom();

        IRootDock rebuilt;
        if (rooms.Count == 0)
        {
            rebuilt = _dockBuilder.CreateAutoFit(0, _workspaceAspect, paneId => CreateRebuiltTile(paneId, null, playbackStates));
            keepHero = false;
            keepCustom = false;
        }
        else if (keepHero)
        {
            var index = 0;
            rebuilt = _dockBuilder.CreateHeroAndSideColumn(rooms.Count, paneId =>
            {
                var room = index < rooms.Count ? rooms[index++] : null;
                return CreateRebuiltTile(paneId, room, playbackStates);
            });
            keepHero = rooms.Count > 2;
        }
        else if (keepCustom)
        {
            // 自定义比例：克隆现有结构后删掉目标窗格，保留其余比例与拆分行列。
            var tileByPaneId = remainingTiles.ToDictionary(candidate => candidate.PaneId, StringComparer.Ordinal);
            rebuilt = _dockBuilder.CloneStructure(Layout, paneId =>
            {
                var old = tileByPaneId.GetValueOrDefault(paneId);
                var room = old is null ? null : RoomCatalog.RoomsById.GetValueOrDefault(old.LiveRoomId);
                return CreateRebuiltTile(paneId, room, playbackStates);
            });

            if (!WorkspaceDockBuilder.RemovePane(rebuilt, tile.PaneId))
            {
                // 结构异常时退回自动适配，保证仍然能安全移除。
                var index = 0;
                rebuilt = _dockBuilder.CreateAutoFit(rooms.Count, _workspaceAspect, paneId =>
                {
                    var room = index < rooms.Count ? rooms[index++] : null;
                    return CreateRebuiltTile(paneId, room, playbackStates);
                });
                keepCustom = false;
            }
        }
        else
        {
            var index = 0;
            rebuilt = _dockBuilder.CreateAutoFit(rooms.Count, _workspaceAspect, paneId =>
            {
                var room = index < rooms.Count ? rooms[index++] : null;
                return CreateRebuiltTile(paneId, room, playbackStates);
            });
        }

        var status = keepCustom && rooms.Count > 0
            ? MainWindowStatusText.RoomRemovedKeepingRatio(title)
            : MainWindowStatusText.RoomRemoved(title);

        await ApplyRebuiltLayoutAsync(
            rebuilt,
            audioStates,
            status,
            startPlayback: true,
            isHero: keepHero,
            ct: CancellationToken.None,
            customLayout: keepCustom).ConfigureAwait(true);
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
            .Select(tile => RoomCatalog.RoomsById.GetValueOrDefault(tile.LiveRoomId))
            .Where(room => room is not null)
            .Select(room => room!)
            .ToList();

        await FitRoomsAsync(rooms, rooms.Count == 0 ? MainWindowStatusText.AutoFitReset() : MainWindowStatusText.AutoAdaptedShort(rooms.Count), ct)
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

            player.ShowLoading(MainWindowStatusText.PlayerReconnecting);
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

        await ApplyRebuiltLayoutAsync(layout, audioStates, status, startPlayback, isHero: false, ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 切到「主副屏」布局：第一路作为主画面，其余房间在右侧纵向排列。
    /// 与一键自适应共用重建路径，房间顺序沿用当前工作区窗格顺序；播放器会重新起播以避免 OpenGL 控件搬运。
    /// </summary>
    public async Task ApplyHeroLayoutAsync(CancellationToken ct = default, bool startPlayback = true)
    {
        if (Layout is null)
        {
            return;
        }

        // 与一键自适应一致：自由布局下先切回 Dock 宿主，否则新布局只落在隐藏的 Dock 上，用户看不到效果。
        await ExitFreeformForTiledLayoutAsync().ConfigureAwait(true);

        ExitFocus();
        var rooms = WorkspaceDockBuilder.EnumerateTiles(Layout)
            .Where(tile => tile.HasRoom)
            .Select(tile => RoomCatalog.RoomsById.GetValueOrDefault(tile.LiveRoomId))
            .Where(room => room is not null)
            .Select(room => room!)
            .ToList();

        var (playbackStates, audioStates) = CaptureRoomStates(Layout);
        DisposePlayers(Layout);
        DetachLayoutTracking();

        var index = 0;
        var layout = _dockBuilder.CreateHeroAndSideColumn(rooms.Count, paneId =>
        {
            var room = index < rooms.Count ? rooms[index++] : null;
            return CreateRebuiltTile(paneId, room, playbackStates);
        });

        var status = rooms.Count <= 2
            ? MainWindowStatusText.HeroSideMinimumHint
            : MainWindowStatusText.HeroSideApplied(rooms.Count);
        await ApplyRebuiltLayoutAsync(layout, audioStates, status, startPlayback, isHero: rooms.Count > 2, ct)
            .ConfigureAwait(true);
    }

    private async Task ApplyRebuiltLayoutAsync(
        IRootDock layout,
        IReadOnlyDictionary<Guid, TileAudioSnapshot> audioStates,
        string status,
        bool startPlayback,
        bool isHero,
        CancellationToken ct,
        bool customLayout = false)
    {
        _suppressLayoutTracking = true;
        _dockBuilder.InitLayout(layout);
        Layout = layout;
        var dimensions = WorkspaceDockBuilder.GetGridDimensions(layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        SetHeroLayout(isHero);
        SetCustomLayout(customLayout);
        LayoutProfiles.MarkDirty();
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        NotifyWorkspaceSummaryChanged();
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
            RoomCatalog.RoomsById.TryGetValue(old?.LiveRoomId ?? Guid.Empty, out var existingRoom);
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
        LayoutProfiles.MarkDirty();
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        NotifyWorkspaceSummaryChanged();
        StatusText = MainWindowStatusText.PaneSplitAdded(room.Alias);

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

    private void SetHeroLayout(bool value)
    {
        if (_isHeroLayout == value)
        {
            return;
        }

        IsHeroLayout = value;
    }

    private void AttachLayoutTracking(IRootDock layout)
    {
        DetachLayoutTracking();
        _layoutTrackingArmed = false;
        _trackedLayout = layout;
        foreach (var dockable in WorkspaceLayoutCoordinator.EnumerateDockables(layout))
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

        foreach (var dockable in WorkspaceLayoutCoordinator.EnumerateDockables(_trackedLayout))
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
        LayoutProfiles.MarkDirty();
        StatusText = MainWindowStatusText.CustomRatioEnabled();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toastCts?.Cancel();
        _healthTimer?.Stop();
        _healthTimer = null;
        _roomStatusTimer?.Stop();
        _roomStatusTimer = null;
        RoomCatalog.Dispose();
        _focusHintTimer?.Stop();
        _focusHintTimer = null;
        ReleaseDanmakuSubscriptions();
        ExitFocus();
        DetachLayoutTracking();
        DisposePlayers(Layout);
        _players.Dispose();
        SystemStatus.Dispose();

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
            _clipStudio?.Dispose();
            _clipStudio = null;
        }
        catch
        {
            // 剪辑台释放失败不阻塞窗口关闭。
        }

        // 基础设施单例（剪辑导出/ASR → 录制引擎 → 弹幕 → 插件宿主/HttpClient → SQLite 连接池）
        // 由组合根按与创建相反的顺序统一释放，ViewModel 只负责视图态退订与播放器控件销毁。
        // AppServices.Dispose 内部幂等且各步骤不抛异常，不会阻塞窗口关闭。
        _services.Dispose();
    }

    private async Task LoadProfileAsync(LayoutProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        ExitFocus();
        // V0.7：自由布局方案以 version=2 JSON 存在同一列，先识别再决定是否走 Dock 反序列化。
        var freeformDocument = FreeformLayoutDocument.LooksLikeFreeform(profile.DockLayoutJson)
            ? FreeformLayoutDocument.TryParse(profile.DockLayoutJson)
            : null;
        if (freeformDocument is null && IsFreeformMode)
        {
            // 从自由画布切换到平铺方案：同样要先让视图把宿主切回 Dock，再替换布局树。
            await ExitFreeformForTiledLayoutAsync().ConfigureAwait(true);
        }

        var previousLayout = Layout;
        var previousTiles = WorkspaceDockBuilder.EnumerateTiles(previousLayout)
            .Where(tile => tile.HasRoom)
            .ToList();
        var previousByRoom = previousTiles
            .GroupBy(tile => tile.LiveRoomId)
            .ToDictionary(group => group.Key, group => group.First());
        var previousRoomIds = previousByRoom.Keys.ToList();

        IRootDock? layout = null;

        if (freeformDocument is null && !string.IsNullOrWhiteSpace(profile.DockLayoutJson))
        {
            try
            {
                layout = _serializer.Deserialize<IRootDock>(profile.DockLayoutJson);
            }
            catch (Exception ex)
            {
                StatusText = MainWindowStatusText.ProfileDeserializeFailed(profile.Name, ex.Message);
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
                RoomCatalog.RoomsById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
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
            RoomCatalog.RoomsById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
            return new RoomTileViewModel(paneId, room)
            {
                IsDanmakuVisible = slot?.DefaultDanmakuVisible ?? false
            };
        });

        var newTiles = WorkspaceDockBuilder.EnumerateTiles(layout)
            .Where(tile => tile.HasRoom)
            .ToList();

        var reusePanes = previousLayout is not null && WorkspaceLayoutCoordinator.HasSamePaneIds(previousLayout, layout);
        var enteringFreeform = freeformDocument is not null && !IsFreeformMode;
        // 目标是自由布局且宿主结构会变化（从平铺进入，或自由方案重建）时，必须先让视图切宿主：
        // 否则翻转模式后画布立刻显现，同一个 MpvPlayerView 会被 Dock 与画布两个 ContentPresenter 争用而闪退。
        var rebuildingFreeform = freeformDocument is not null && IsFreeformMode && !reusePanes;
        if ((enteringFreeform || rebuildingFreeform) && PrepareFreeformHostAsync is { } prepareHost)
        {
            await prepareHost().ConfigureAwait(true);
        }

        IsFreeformMode = freeformDocument is not null;
        _freeformDocument = freeformDocument;

        // 两个方案的窗格集合一致时，不替换 Dock 树，直接复用原窗格和 PlayerView。
        // 这是切换方案最常见的情况，也是零黑屏的关键：控件不移动，底层 MpvContext 不断流。
        if (previousLayout is not null && reusePanes)
        {
            // 从平铺进入自由布局时宿主已切换、播放器已解绑，必须等画布挂好窗格后再统一起播。
            var startPlayersImmediately = !enteringFreeform;
            var slotsByPane = profile.Slots.ToDictionary(slot => slot.PaneId);
            foreach (var oldTile in WorkspaceDockBuilder.EnumerateTiles(previousLayout))
            {
                slotsByPane.TryGetValue(oldTile.PaneId, out var slot);
                RoomCatalog.RoomsById.TryGetValue(slot?.LiveRoomId ?? Guid.Empty, out var room);
                oldTile.IsDanmakuVisible = slot?.DefaultDanmakuVisible ?? false;
                if (oldTile.LiveRoomId == (room?.Id ?? Guid.Empty))
                {
                    if (startPlayersImmediately && oldTile.PlayerView is null && oldTile.HasRoom)
                    {
                        // 宿主刚从自由画布切回 Dock：旧控件已在画布侧解绑，必须重新挂载播放器。
                        _ = _players.StartAsync(oldTile);
                    }

                    continue;
                }

                _players.RemovePlayer(oldTile);

                if (room is null)
                {
                    oldTile.ClearRoom();
                    continue;
                }

                oldTile.Assign(room);
                if (startPlayersImmediately)
                {
                    _ = _players.StartAsync(oldTile);
                }
            }

            LayoutProfiles.NotifyProfileLoaded(profile);
            SetCustomLayout(profile.IsCustomLayout);
            SetHeroLayout(WorkspaceDockBuilder.ContainsDockId(previousLayout, "workspace-hero"));
            if (freeformDocument is not null)
            {
                if (enteringFreeform)
                {
                    // 画布此前为空：先按当前窗格建子项，再套自由布局文档。
                    NotifyWorkspaceTilesChanged();
                }

                FreeformApplyRequested?.Invoke(this, freeformDocument);
            }

            WireTileInteractions(previousLayout);
            var oldDimensions = WorkspaceDockBuilder.GetGridDimensions(previousLayout);
            GridRows = oldDimensions.Rows;
            GridColumns = oldDimensions.Columns;
            LayoutProfiles.MarkClean();
            StatusText = LayoutLoadStatusLogic.InPlace(profile.Name, freeformDocument is not null);
            SyncDanmakuSubscriptions();
            if (enteringFreeform)
            {
                // 画布刚显现，等一轮布局让窗格视图套上模板后再起播，避免播放器控件还没挂到视觉树。
                await Task.Delay(120).ConfigureAwait(true);
                await StartWorkspacePlayersAsync().ConfigureAwait(true);
            }

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
        SetHeroLayout(WorkspaceDockBuilder.ContainsDockId(layout, "workspace-hero"));
        if (freeformDocument is not null)
        {
            FreeformApplyRequested?.Invoke(this, freeformDocument);
        }

        LayoutProfiles.MarkClean();
        _suppressLayoutTracking = false;
        AttachLayoutTracking(layout);
        WireTileInteractions(layout);
        NotifyWorkspaceSummaryChanged();

        // 差集只销毁被移除的房间；交集播放器已转移，不重新拉流。
        foreach (var roomId in diff.Removed)
        {
            if (!previousByRoom.TryGetValue(roomId, out var oldTile))
            {
                continue;
            }

            _players.RemovePlayer(oldTile);
        }

        StatusText = LayoutLoadStatusLogic.Describe(
            profile.Name,
            rebuilt,
            diff.HasChanges,
            newTiles.Count,
            transferred,
            diff.Removed.Count,
            diff.Added.Count);

        await Task.Yield();
        if (enteringFreeform || rebuildingFreeform)
        {
            // 画布刚显现/重建，等一轮布局让窗格视图套上模板后再创建播放器。
            await Task.Delay(120).ConfigureAwait(true);
        }

        foreach (var tile in newTiles.Where(tile => tile.PlayerView is null))
        {
            await _players.StartAsync(tile).ConfigureAwait(true);
        }

        SyncDanmakuSubscriptions();
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

        var samples = new List<PaneHealthSample>();
        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout))
        {
            var player = tile.PlayerView;
            if (player is null)
            {
                continue;
            }

            var metrics = player.GetHealthMetrics();
            samples.Add(new PaneHealthSample(
                tile.PaneId,
                metrics.DecoderDroppedFrames + metrics.DroppedFrames,
                metrics.FramesPerSecond));
        }

        SystemStatus.UpdateHealth(samples);

        SyncRoomStatuses();
    }

    // V0.6：独立轮询主库所有房间的开播状态，避免「只有放进工作区才显示状态」。
    private void StartRoomStatusMonitor()
    {
        if (_roomStatusTimer is not null)
        {
            return;
        }

        _roomStatusTimer = new DispatcherTimer { Interval = RoomStatusPollInterval };
        _roomStatusTimer.Tick += async (_, _) => await RoomCatalog.RefreshLiveStatusesAsync().ConfigureAwait(true);
        _roomStatusTimer.Start();

        // 启动后立即刷新一轮，避免首屏长时间停留在「状态未知」。
        _ = RoomCatalog.RefreshLiveStatusesAsync();
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
                RoomStatusText.Offline => false,
                RoomStatusText.PlaybackFailed => false,
                _ => tile.Status?.IsLive
            };

            if (live is not null)
            {
                RoomCatalog.RecordLiveStatus(tile.LiveRoomId, live);
            }
        }

        RoomCatalog.ApplyLiveStatuses();

        // 工作区播放状态也能提供开播信息：录制中心打开时立即套用同一套排序。
        _recordingCenter?.RefreshLiveStatuses();
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
            _players.RemovePlayer(tile);
        }
    }

    /// <summary>创建「默认方案」时使用的 2×2 空布局 JSON（原初始化内联逻辑）。</summary>
    private string CreateDefaultProfileLayoutJson()
    {
        var layout = _dockBuilder.CreateGrid(2, 2, paneId => new RoomTileViewModel(paneId, null));
        return SerializeLayout(layout);
    }

    // ---------------------------------------------------------------------
    // 子视图模型宿主实现：窗口级副作用（状态栏、工作区清理、播放器生命周期）
    // 仍留在主 VM，并在与抽离前相同的时机、以相同文案执行。
    // ---------------------------------------------------------------------

    void IRoomCatalogHost.SetStatus(string message) => SetStatus(message);

    void IRoomCatalogHost.MarkDirty() => MarkDirty();

    void IRoomCatalogHost.NotifyWorkspaceSummaryChanged() => NotifyWorkspaceSummaryChanged();

    void IRoomCatalogHost.RefreshRecordingCenterLiveStatuses() => _recordingCenter?.RefreshLiveStatuses();

    async Task<bool> IRoomCatalogHost.TryStopRecordingForDeleteAsync(LiveRoom room, CancellationToken ct)
    {
        if (!_services.Recordings.IsRecording(room.Id))
        {
            return true;
        }

        try
        {
            await _services.Recordings.StopAsync(room.Id, ct).ConfigureAwait(true);
            SetStatus(MainWindowStatusText.RecordingStopped(room.Alias));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(MainWindowStatusText.RecordingStopFailedCancelingDelete(ex.Message));
            return false;
        }
    }

    void IRoomCatalogHost.DetachRoomFromWorkspace(Guid roomId)
    {
        if (Layout is null)
        {
            return;
        }

        foreach (var tile in WorkspaceDockBuilder.EnumerateTiles(Layout).Where(tile => tile.LiveRoomId == roomId).ToList())
        {
            if (_focusedTile == tile)
            {
                ExitFocus();
            }

            _players.RemovePlayer(tile);
            tile.ClearRoom();
        }
    }

    Task IRoomCatalogHost.ReflowWorkspaceAsync(CancellationToken ct) => ReflowAutoAsync(ct);

    void IRoomCatalogHost.SyncDanmakuSubscriptions() => SyncDanmakuSubscriptions();

    WorkspaceLayoutSnapshot? IWorkspaceLayoutProfileHost.CaptureSnapshot()
    {
        if (Layout is null)
        {
            return null;
        }

        var dimensions = WorkspaceDockBuilder.GetGridDimensions(Layout);
        GridRows = dimensions.Rows;
        GridColumns = dimensions.Columns;
        return new WorkspaceLayoutSnapshot(
            SerializeCurrentLayout(Layout),
            WorkspaceDockBuilder.EnumerateTiles(Layout).ToList(),
            IsCustomLayout,
            IsFreeformMode);
    }

    Task IWorkspaceLayoutProfileHost.LoadProfileAsync(LayoutProfile profile) => LoadProfileAsync(profile);

    void IWorkspaceLayoutProfileHost.ClearCustomLayoutSnapshot() => ClearCustomLayoutSnapshot();

    void IWorkspaceLayoutProfileHost.SetStatus(string message) => SetStatus(message);
}
