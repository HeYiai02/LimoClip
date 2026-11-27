using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Settings;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Desktop.Controls;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using DockControl = Dock.Avalonia.Controls.DockControl;
using Point = Avalonia.Point;
using DataObject = Avalonia.Input.DataObject;
using DragDropEffects = Avalonia.Input.DragDropEffects;

namespace LiveMonitor.Desktop.Views;

/// <summary>
/// 主壳视图（code-behind）。只负责 Avalonia 视觉树与窗口交互：Dock/自由画布宿主挂载与切换、
/// 窗口外壳与沉浸/全屏 chrome、系统对话框（StorageProvider）、指针手势与事件转发。
/// 业务状态与数据流转在 <see cref="MainWindowViewModel"/> 及更下层服务；边界规则见 ARCHITECTURE.md §10.7。
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private AppServices? _services;
    private bool _initializing;
    private bool _sidebarVisible = true;
    private bool _isDesktopFullscreen;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private Guid? _lastRoomClickId;
    private DateTime _lastRoomClickAt;
    private readonly DispatcherTimer _immersiveIdleTimer;
    private bool _isImmersiveActive;

    /// <summary>沉浸模式下指针是否处于空闲（控制条已隐藏），新建的浮层播放器据此初始化。</summary>
    private bool _isImmersiveIdle;
    private DateTime _lastFullscreenToggleAt;
    private IRootDock? _savedDockLayout;
    private RoomListItemViewModel? _roomDragCandidate;
    private Point _roomDragStart;
    private bool _roomDragArmed;
    private bool _roomDragActive;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        Dock.SizeChanged += OnDockSizeChanged;
        Dock.AddHandler(InputElement.PointerPressedEvent, OnDockPointerPressed, RoutingStrategies.Tunnel);
        RoomList.AddHandler(InputElement.PointerPressedEvent, OnRoomPointerPressed, RoutingStrategies.Tunnel);
        RoomList.AddHandler(InputElement.PointerMovedEvent, OnRoomPointerMoved, RoutingStrategies.Tunnel);
        RoomList.AddHandler(InputElement.PointerReleasedEvent, OnRoomPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += OnWindowKeyDown;
        PointerMoved += OnWindowPointerActivity;
        PointerPressed += OnWindowPointerActivity;
        PointerWheelChanged += OnWindowPointerActivity;

        _immersiveIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _immersiveIdleTimer.Tick += (_, _) =>
        {
            _immersiveIdleTimer.Stop();
            if (_isImmersiveActive)
            {
                HideImmersiveControls();
            }
        };
    }

    /// <summary>
    /// 无边框一体化顶栏：空白区域支持按住左键拖动窗口，双击在最大化/还原之间切换。
    /// 命中按钮、下拉框、输入框等交互控件时直接放行，避免第一次点击被拖动吞掉、
    /// 第二次点击又被当成顶栏双击而误触最大化（方案下拉框需要双击才能展开的根因）。
    /// </summary>
    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsTopBarInteractiveSource(e.Source))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 指针状态或平台实现不允许拖动时忽略，不影响顶栏其它控件。
        }
    }

    /// <summary>事件源是否落在顶栏内需要自行处理点击的控件上（按钮 / 下拉框 / 输入框 / 滑块等）。</summary>
    private bool IsTopBarInteractiveSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            switch (current)
            {
                case Avalonia.Controls.ComboBox:
                case Avalonia.Controls.TextBox:
                case Avalonia.Controls.Slider:
                case Avalonia.Controls.Primitives.ScrollBar:
                case Avalonia.Controls.TabItem:
                case Avalonia.Controls.Button:
                    return true;
                default:
                    if (ReferenceEquals(current, TopBar))
                    {
                        return false;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Dock 11.3.12.1 默认把比例分割线解析成 GridSplitter（0x0、不可拖），
    /// 这里在内容 presenter 上显式注册模型到控件的模板，确保拖动条真正出现。
    /// </summary>
    private void EnsureSplitterTemplate()
    {
        var content = Dock.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .FirstOrDefault(control => control.Name == "PART_ContentPresenter");
        if (content is null)
        {
            return;
        }

        if (content.DataTemplates.Any(template => template is FuncDataTemplate<ProportionalDockSplitter>))
        {
            return;
        }

        content.DataTemplates.Insert(0,
            new FuncDataTemplate<ProportionalDockSplitter>((_, _) => new ProportionalStackPanelSplitter()));
    }

    /// <summary>
    /// 恢复比例时只改了模型上的 Proportion；这里把模型值直接写到可见的
    /// ContentPresenter 附加属性，绕开模型绑定不刷新 panel 的情况。
    /// </summary>
    private void SyncVisualProportions()
    {
        foreach (var itemsControl in Dock.GetVisualDescendants().OfType<ItemsControl>())
        {
            if (itemsControl.Name != "PART_ItemsControl" ||
                itemsControl.ItemsPanelRoot is not ProportionalStackPanel panel ||
                itemsControl.DataContext is not IDock dock ||
                dock.VisibleDockables is null)
            {
                continue;
            }

            var count = Math.Min(panel.Children.Count, dock.VisibleDockables.Count);
            for (var i = 0; i < count; i++)
            {
                ProportionalStackPanel.SetProportion(panel.Children[i], dock.VisibleDockables[i].Proportion);
            }

            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }

        Dock.InvalidateMeasure();
        Dock.InvalidateArrange();
    }

    private void OnDockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel?.BeginCustomLayoutTracking();
    }

    private void OnDockSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _viewModel?.SetWorkspaceAspect(Dock.Bounds.Width, Dock.Bounds.Height);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {        if (_initializing || _viewModel is not null)
        {
            return;
        }

        _initializing = true;
        try
        {
            var services = await AppServices.CreateAsync();
            _services = services;
            _viewModel = new MainWindowViewModel(services);
            _viewModel.ConfirmProfileSwitchAsync = async () =>
            {
                var dialog = new UnsavedChangesWindow();
                return await dialog.ShowDialog<ProfileSwitchChoice>(this);
            };
            _viewModel.ConfirmRecordingDeleteAsync = async item =>
            {
                var dialog = new ConfirmDeleteRecordingWindow(item);
                return await dialog.ShowDialog<bool>(this);
            };
            _viewModel.PickRecordingDirectoryAsync = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = MainWindowStatusText.PickRecordingDirectoryTitle,
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };
            _viewModel.PickClipVideoFileAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = MainWindowStatusText.PickClipVideoFileTitle,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(MainWindowStatusText.VideoFileTypeName)
                        {
                            Patterns = new[] { "*.mkv", "*.mp4" }
                        }
                    }
                });
                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };
            _viewModel.PickClipExportDirectoryAsync = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = MainWindowStatusText.PickClipExportDirectoryTitle,
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };
            DataContext = _viewModel;
            _viewModel.ProportionsRestored += (_, _) => SyncVisualProportions();
            _viewModel.Groups.CollectionChanged += (_, _) => RefreshGroupPills();
            _viewModel.WorkspaceTilesChanged += (_, _) => SyncFreeformTiles();
            _viewModel.LayoutStructureChanged += (_, _) => RefreshWorkspaceHost();
            _viewModel.LeaveFreeformHostAsync = LeaveFreeformHostAsync;
            FreeformCanvas.RoomDropped += OnFreeformRoomDropped;
            _viewModel.FreeformApplyRequested += (_, document) =>
            {
                FreeformCanvas.ApplyDocument(document);
            };
            FreeformCanvas.LayoutChanged += (_, _) =>
            {
                _viewModel.UpdateFreeformDocument(FreeformCanvas.CaptureDocument());
            };
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.Layout))
                {
                    // 自由布局模式下由画布承载窗格，Dock 保持空布局，避免控件被两个宿主同时挂载。
                    if (!_viewModel.IsFreeformMode)
                    {
                        Dock.Layout = _viewModel.Layout;
                    }
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.IsFocused))
                {
                    ApplyShellLayout();
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.IsRecordingCenterVisible))
                {
                    ApplyShellLayout();
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.IsClipStudioVisible))
                {
                    ApplyShellLayout();
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.GroupFilter))
                {
                    RefreshGroupPills();
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.FocusPlayer))
                {
                    // 浮层播放器在 IsFocused 变更之后才创建，视觉树遍历赶不上它；
                    // 创建时按当前空闲状态初始化控制条，否则全屏下控制条会一直挂着。
                    if (_isImmersiveActive)
                    {
                        _viewModel.FocusPlayer?.SetChromeVisible(!_isImmersiveIdle);
                    }
                }
            };
            await _viewModel.InitializeAsync();
            RefreshGroupPills();
            _viewModel.Metrics.Start();
            _viewModel.SetWorkspaceAspect(Dock.Bounds.Width, Dock.Bounds.Height);
            if (_viewModel.IsFreeformMode)
            {
                // 载入的是自由布局方案：Dock 不承载窗格，只保留结构用于恢复平铺。
                _savedDockLayout = _viewModel.Layout;
                Dock.Layout = null;
            }
            else
            {
                Dock.Layout = _viewModel.Layout;
            }

            EnsureSplitterTemplate();
            SyncFreeformTiles();
        }
        catch (Exception ex)
        {
            var details = ex.ToString();
            try
            {
                var log = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
                var header = $"time={DateTimeOffset.Now:O}{Environment.NewLine}" +
                             $"cwd={Environment.CurrentDirectory}{Environment.NewLine}" +
                             $"LIVEMONITOR_DB={Environment.GetEnvironmentVariable("LIVEMONITOR_DB")}{Environment.NewLine}" +
                             $"LOCALAPPDATA={Environment.GetEnvironmentVariable("LOCALAPPDATA")}{Environment.NewLine}" +
                             $"{Environment.NewLine}";
                File.WriteAllText(log, header + details);
            }
            catch
            {
                // 启动失败时日志写不出去也不能再抛异常。
            }

            var dialog = new StartupErrorWindow(MainWindowStatusText.StartupFailedTitle, details);
            await dialog.ShowDialog(this);
        }
        finally
        {
            _initializing = false;
        }
    }


    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 剪辑台内的 libmpv 上下文不属于窗口视觉树自动释放，先显式停播再销毁。
        try
        {
            ClipStudioViewHost?.Shutdown();
        }
        catch
        {
            // 关闭阶段播放器收尾失败不阻塞退出。
        }

        // ViewModel 负责视图态退订并调用 AppServices.Dispose 统一释放基础设施。
        _viewModel?.Dispose();

        // 兜底：启动阶段 ViewModel 构造失败时窗口仍持有 AppServices，同样按组合根释放（幂等）。
        _services?.Dispose();
    }

    /// <summary>
    /// 按「焦点模式 / 录制中心 / 剪辑台 / 普通工作区」统一布局外壳：
    /// 录制中心与剪辑台隐藏侧栏与底部状态栏但保留顶栏切换，且不销毁 Dock 与播放器。
    /// </summary>
    private void ApplyShellLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        var recording = _viewModel.IsRecordingCenterVisible;
        var clipping = _viewModel.IsClipStudioVisible;
        var overlay = recording || clipping;
        var focused = !overlay && _viewModel.IsFocused;
        // 桌面级全屏：与焦点模式类似地隐藏外壳，但保留整个工作区（多路窗格）而不是单路。
        var fullscreenShell = !overlay && !focused && _isDesktopFullscreen;
        var sidebarVisible = !overlay && !focused && !fullscreenShell && _sidebarVisible;

        Sidebar.IsVisible = sidebarVisible;
        TopBar.IsVisible = !focused && !fullscreenShell;
        StatusBar.IsVisible = !overlay && !focused && !fullscreenShell;
        UpdateImmersiveMode(focused || fullscreenShell);
        RootGrid.ColumnDefinitions[0].Width =
            sidebarVisible ? new GridLength(260) : new GridLength(0);
        RootGrid.RowDefinitions[0].Height =
            focused || fullscreenShell ? new GridLength(0) : new GridLength(48);
        RootGrid.RowDefinitions[2].Height =
            overlay || focused || fullscreenShell ? new GridLength(0) : new GridLength(28);

        // 真正的全屏：焦点模式/桌面全屏下把工作区四周的 6px 留白也去掉，画面铺满整屏。
        WorkspaceHost.Padding = focused || fullscreenShell
            ? new Thickness(0)
            : new Thickness(6);

        WorkspaceTabButton.Classes.Set("active", !overlay);
        RecordingTabButton.Classes.Set("active", recording);
        ClipTabButton.Classes.Set("active", clipping);

        Dock.InvalidateMeasure();
        Dock.InvalidateArrange();
        Dock.UpdateLayout();
    }

    /// <summary>
    /// 沉浸模式（桌面全屏 / 焦点模式）：鼠标活动时临时显示窗格控制条与顶部提示，
    /// 指针空闲 2.5 秒后自动隐藏，避免全屏观看时控件常驻。
    /// </summary>
    private void UpdateImmersiveMode(bool immersive)
    {
        if (_isImmersiveActive == immersive)
        {
            return;
        }

        _isImmersiveActive = immersive;
        if (immersive)
        {
            ShowImmersiveControls();
            RestartImmersiveIdleTimer();
        }
        else
        {
            _isImmersiveIdle = false;
            _immersiveIdleTimer.Stop();
            Classes.Set("immersive-idle", false);
            SetPlayerControlBarsVisible(true);
            DesktopFullscreenHint.IsVisible = false;
        }
    }

    private void OnWindowPointerActivity(object? sender, PointerEventArgs e)
    {
        if (!_isImmersiveActive)
        {
            return;
        }

        ShowImmersiveControls();
        if (_isDesktopFullscreen)
        {
            // 顶部提示只在指针靠近顶部时出现，避免全屏时一直挡着画面。
            DesktopFullscreenHint.IsVisible = e.GetPosition(this).Y <= 90;
        }

        RestartImmersiveIdleTimer();
    }

    private void ShowImmersiveControls()
    {
        _isImmersiveIdle = false;
        Classes.Set("immersive-idle", false);
        SetPlayerControlBarsVisible(true);
        if (_isDesktopFullscreen)
        {
            DesktopFullscreenHint.IsVisible = true;
        }
    }

    private void HideImmersiveControls()
    {
        _isImmersiveIdle = true;
        Classes.Set("immersive-idle", true);
        SetPlayerControlBarsVisible(false);
        DesktopFullscreenHint.IsVisible = false;
    }

    /// <summary>
    /// 逐个窗格切换底部播放控制条（MpvPlayerView 的 ControlBar）显示状态。
    /// Dock 里的播放器不在 MainWindow 的样式选择器作用域内，因此直接遍历视觉树处理。
    /// </summary>
    private void SetPlayerControlBarsVisible(bool visible)
    {
        // 平铺模式窗格在 Dock 视觉树里，自由布局模式窗格在 FreeformCanvas 里，两个宿主都要处理。
        var players = Dock.GetVisualDescendants()
            .OfType<Player.MpvPlayerView>()
            .Concat(FreeformCanvas.GetVisualDescendants().OfType<Player.MpvPlayerView>())
            .ToList();

        // 焦点模式的全屏浮层播放器挂在 ContentControl 内容上，不属于上面两个宿主，需要单独补上，
        // 否则沉浸空闲后它收不到隐藏通知，全屏观看时控制条会一直停在画面底部。
        if (_viewModel?.FocusPlayer is { } focusPlayer && !players.Contains(focusPlayer))
        {
            players.Add(focusPlayer);
        }

        foreach (var player in players)
        {
            player.SetChromeVisible(visible);
        }
    }

    private void RestartImmersiveIdleTimer()
    {
        _immersiveIdleTimer.Stop();
        _immersiveIdleTimer.Start();
    }

    /// <summary>
    /// 桌面级全屏：连同顶栏/侧栏/状态栏一起隐藏，只留工作区。
    /// 与“焦点模式”不同，这里不改变 Dock 布局，多路窗格仍按当前方案显示。
    /// </summary>
    private void ToggleDesktopFullscreen()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_isDesktopFullscreen)
        {
            ExitDesktopFullscreen();
            return;
        }

        _windowStateBeforeFullscreen = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState;
        _isDesktopFullscreen = true;
        WindowState = WindowState.FullScreen;
        ApplyShellLayout();
        _viewModel.SetStatus(MainWindowStatusText.DesktopFullscreenEntered);
    }

    private void ExitDesktopFullscreen()
    {
        if (!_isDesktopFullscreen)
        {
            return;
        }

        _isDesktopFullscreen = false;
        var restore = _windowStateBeforeFullscreen == WindowState.FullScreen
            ? WindowState.Normal
            : _windowStateBeforeFullscreen;
        WindowState = restore;
        ApplyShellLayout();
        _viewModel?.SetStatus(MainWindowStatusText.DesktopFullscreenExited);
    }

    private void OnToggleDesktopFullscreen(object? sender, RoutedEventArgs e)
    {
        ToggleDesktopFullscreen();
    }

    private void OnExitDesktopFullscreen(object? sender, RoutedEventArgs e)
    {
        ExitDesktopFullscreen();
    }

    private async void OnHeroLayout(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _viewModel.ApplyHeroLayoutAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.HeroLayoutSwitchFailed(ex.Message));
        }
    }

    /// <summary>把当前工作区窗格同步给自由画布（窗格增减/房间变化时调用）。</summary>
    private void SyncFreeformTiles()
    {
        if (_viewModel is null)
        {
            return;
        }

        FreeformCanvas.SetTiles(_viewModel.WorkspaceTiles);
        if (_viewModel.FreeformDocument is { } document)
        {
            FreeformCanvas.ApplyDocument(document);
        }
    }

    /// <summary>窗格结构被模型层修改（例如把房间移出工作区）后重建宿主，避免留下空窗格。</summary>
    private void RefreshWorkspaceHost()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.IsFreeformMode)
        {
            SyncFreeformTiles();
            _viewModel.UpdateFreeformDocument(FreeformCanvas.CaptureDocument());
            return;
        }

        // Dock 绑定已被本地赋值覆盖，这里直接重建布局并补齐分隔条模板。
        Dock.Layout = null;
        Dock.Layout = _viewModel.Layout;
        EnsureSplitterTemplate();
        Dock.UpdateLayout();
    }

    /// <summary>左侧房间被拖到自由画布：已加入则移动位置，否则先加入工作区再落到指定位置。</summary>
    private async void OnFreeformRoomDropped(object? sender, RoomDropEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var tile = _viewModel.WorkspaceTiles.FirstOrDefault(t => t.LiveRoomId == e.RoomId);
            if (tile is null)
            {
                await _viewModel.AddRoomToWorkspaceAsync(e.RoomId);
                tile = _viewModel.WorkspaceTiles.FirstOrDefault(t => t.LiveRoomId == e.RoomId);
            }

            if (tile is null)
            {
                return;
            }

            FreeformCanvas.SetPlacement(tile.PaneId, e.Rect);
            // 新拖入的窗格必须立刻置顶：否则它与背景大窗同层时，左键/右键会命中大窗。
            FreeformCanvas.BringToFront(tile.PaneId);
            _viewModel.UpdateFreeformDocument(FreeformCanvas.CaptureDocument());
            _viewModel.SetStatus(MainWindowStatusText.PlacementApplied(tile.DisplayTitle));
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.DropIntoWorkspaceFailed(ex.Message));
        }
    }

    private async void OnToggleFreeformLayout(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _initializing)
        {
            return;
        }

        try
        {
            if (_viewModel.IsFreeformMode)
            {
                // 退出自由布局：先把宿主切回 Dock 并重新挂载播放器。
                await LeaveFreeformHostAsync();
                _viewModel.ToggleFreeformMode();
                await _viewModel.StartWorkspacePlayersAsync();
            }
            else
            {
                // 进入自由布局：先拆 Dock 树并等旧窗格视图释放，再解绑播放器、挂到画布，最后起播。
                _savedDockLayout = _viewModel.Layout;
                Dock.Layout = null;
                Dock.IsVisible = false;
                Dock.UpdateLayout();
                await Task.Delay(120);

                _viewModel.DetachWorkspacePlayers();
                _viewModel.ToggleFreeformMode();
                FreeformCanvas.IsVisible = true;
                FreeformCanvas.SetTiles(_viewModel.WorkspaceTiles);
                if (_viewModel.FreeformDocument is { } document)
                {
                    FreeformCanvas.ApplyDocument(document);
                }

                FreeformCanvas.UpdateLayout();
                await Task.Delay(120);
                await _viewModel.StartWorkspacePlayersAsync();
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.FreeformLayoutToggleFailed(ex.Message));
        }
    }

    /// <summary>
    /// 把工作区宿主从自由画布切回平铺 Dock（只做视图层清理：不翻转 ViewModel 模式、不起播）。
    /// ViewModel 在「一键自适应 / 主副屏 / 恢复比例 / 切换到平铺方案」前通过 LeaveFreeformHostAsync 回调它，
    /// 避免播放器被销毁在仍可见的画布视觉树上导致闪退，也避免新布局落在隐藏的 Dock 上导致“没有效果”。
    /// </summary>
    private async Task LeaveFreeformHostAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 先清空画布并等一轮布局，确保旧窗格视图不再绑定 PlayerView；
        // 播放器不能在两个宿主之间复用，必须解绑后由调用方重新挂载。
        FreeformCanvas.SetTiles(Array.Empty<RoomTileViewModel>());
        FreeformCanvas.IsVisible = false;
        FreeformCanvas.UpdateLayout();
        await Task.Delay(120);

        _viewModel.DetachWorkspacePlayers();
        Dock.Layout = _savedDockLayout ?? _viewModel.Layout;
        Dock.Bind(DockControl.LayoutProperty,
            new Avalonia.Data.Binding(nameof(MainWindowViewModel.Layout)));
        Dock.IsVisible = !_viewModel.IsWorkspaceEmpty;
        EnsureSplitterTemplate();
        Dock.UpdateLayout();
        await Task.Delay(120);

        // Dock 刚恢复可见，重新采样一次宽高比，供随后的自适应重建使用。
        _viewModel.SetWorkspaceAspect(Dock.Bounds.Width, Dock.Bounds.Height);
    }

    private async void OnFreeformPipPreset(object? sender, RoutedEventArgs e)
    {        if (_viewModel is null || _initializing)
        {
            return;
        }

        if (!_viewModel.IsFreeformMode)
        {
            OnToggleFreeformLayout(sender, e);
            await Task.Delay(200);
            if (!_viewModel.IsFreeformMode)
            {
                return;
            }
        }

        _viewModel.ApplyFreeformPreset(FreeformPreset.PictureInPicture);
    }

    private void OnSwitchToWorkspace(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SwitchToWorkspaceCommand.Execute(null);
    }

    private void OnSwitchToRecordingCenter(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SwitchToRecordingCenterCommand.Execute(null);
    }

    private void OnSwitchToClipStudio(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SwitchToClipStudioCommand.Execute(null);
    }

    private void OnFocusDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.IsFocused == true)
        {
            e.Handled = true;
            _viewModel.ExitFocus();
        }
    }

    private void OnExitFocus(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ExitFocus();
    }

    private void OnToggleMasterMute(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ToggleMasterMute();
    }

    private void OnWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        if (e.Key == Key.F11)
        {
            // 少数键盘/自动化输入会在一次按键内重复派发 KeyDown，这里做 400ms 去抖，
            // 保证用户按一次 F11 只切换一次桌面全屏。
            var now = DateTime.UtcNow;
            if (now - _lastFullscreenToggleAt < TimeSpan.FromMilliseconds(400))
            {
                e.Handled = true;
                return;
            }

            _lastFullscreenToggleAt = now;
            ToggleDesktopFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FreeformCanvas.IsInteracting)
        {
            FreeformCanvas.CancelInteraction();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape
            && (viewModel.IsRecordingCenterVisible || viewModel.IsClipStudioVisible))
        {
            viewModel.SwitchToWorkspaceCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isDesktopFullscreen)
        {
            ExitDesktopFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsFocused)
        {
            viewModel.ExitFocus();
            e.Handled = true;
            return;
        }

        // V0.4 剪辑台快捷键：Space 播放/暂停、←/→ 5 秒（Shift 1 秒）、I/O 设入出点。
        if (!viewModel.IsClipStudioVisible || IsTextInputFocused())
        {
            return;
        }

        var studio = viewModel.ClipStudio;
        var fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space:
                studio.PlayPause();
                e.Handled = true;
                break;
            case Key.Left:
                studio.SeekRelative(false, fine);
                e.Handled = true;
                break;
            case Key.Right:
                studio.SeekRelative(true, fine);
                e.Handled = true;
                break;
            case Key.I:
                studio.SetInFromPosition();
                e.Handled = true;
                break;
            case Key.O:
                studio.SetOutFromPosition();
                e.Handled = true;
                break;
        }
    }

    /// <summary>焦点在文本输入/下拉框时，不让剪辑台快捷键抢走按键。</summary>
    private bool IsTextInputFocused()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is Avalonia.Controls.TextBox or Avalonia.Controls.ComboBox;
    }

    private async void OnAddRoom(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new AddRoomWindow(_viewModel);
        await dialog.ShowDialog<bool>(this);
    }

    private async void OnEditRoom(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as Avalonia.Controls.Control)?.DataContext is not RoomListItemViewModel item)
        {
            return;
        }

        e.Handled = true;
        var dialog = new EditRoomWindow(item);
        if (await dialog.ShowDialog<bool>(this))
        {
            try
            {
                if (dialog.IsCustomStream)
                {
                    await _viewModel.UpdateRoomAsync(
                        item.Id,
                        dialog.AliasText,
                        dialog.GroupText,
                        dialog.DisplayNumber,
                        dialog.RawUrl,
                        dialog.RefererText,
                        dialog.UserAgentText);
                }
                else
                {
                    await _viewModel.UpdateRoomAsync(item.Id, dialog.AliasText, dialog.GroupText, dialog.DisplayNumber);
                }
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(MainWindowStatusText.RoomUpdateFailed(ex.Message));
            }
        }
    }

    private async void OnDeleteRoom(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as Avalonia.Controls.Control)?.DataContext is not RoomListItemViewModel item)
        {
            return;
        }

        e.Handled = true;
        var dialog = new ConfirmDeleteWindow(item.DisplayTitle);
        if (!await dialog.ShowDialog<bool>(this))
        {
            return;
        }

        try
        {
            await _viewModel.DeleteRoomAsync(item.Id);
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.RoomDeleteFailed(ex.Message));
        }
    }

    private async void OnRoomPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_roomDragActive || _roomDragCandidate is null || _viewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(RoomList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _roomDragCandidate = null;
            _roomDragArmed = false;
            return;
        }

        var position = e.GetPosition(RoomList);
        var deltaX = position.X - _roomDragStart.X;
        var deltaY = position.Y - _roomDragStart.Y;
        if (!_roomDragArmed)
        {
            if (Math.Abs(deltaX) < 4 && Math.Abs(deltaY) < 4)
            {
                return;
            }

            _roomDragArmed = true;
        }

        var room = _roomDragCandidate.Room;
        _roomDragActive = true;
        try
        {
            // TODO(V0.7.5): 迁移到 DataTransfer / DoDragDropAsync（需真机拖放冒烟后单独 PR）。
#pragma warning disable CS0618
            var data = new DataObject();
            data.Set("limoclip/room-id", room.Id.ToString());
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
        }
        finally
        {
            _roomDragActive = false;
            _roomDragCandidate = null;
            _roomDragArmed = false;
        }
    }

    private void OnRoomPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_roomDragActive)
        {
            _roomDragCandidate = null;
            _roomDragArmed = false;
        }
    }

    private async void OnRoomPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var item = (e.Source as Avalonia.Controls.Control)?.DataContext as RoomListItemViewModel
                   ?? RoomList.SelectedItem as RoomListItemViewModel;
        if (item is null)
        {
            return;
        }

        // 左键按下先记为拖拽候选，移动超过 4px 后进入拖放（拖到自由画布可指定落位）。
        if (e.GetCurrentPoint(RoomList).Properties.IsLeftButtonPressed)
        {
            _roomDragCandidate = item;
            _roomDragStart = e.GetPosition(RoomList);
            _roomDragArmed = false;
        }

        var now = DateTime.UtcNow;
        var isDoubleClick = e.ClickCount >= 2 ||
                            (_lastRoomClickId == item.Id &&
                             (now - _lastRoomClickAt).TotalMilliseconds <= 500);
        _lastRoomClickId = item.Id;
        _lastRoomClickAt = now;
        if (!isDoubleClick)
        {
            return;
        }

        e.Handled = true;
        _lastRoomClickId = null;
        try
        {
            await _viewModel.AddRoomToWorkspaceAsync(item.Id);
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private async void OnAutoFit(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            _viewModel.SetWorkspaceAspect(Dock.Bounds.Width, Dock.Bounds.Height);
            await _viewModel.AutoFitAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private async void OnRestoreCustomLayout(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _viewModel.RestoreCustomLayoutAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _viewModel.SaveAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.LayoutSaveFailed(ex.Message));
        }
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var prompt = new TextPromptWindow(
            MainWindowStatusText.ProfileNamePromptLabel,
            _viewModel.SelectedProfile?.Name ?? MainWindowStatusText.NewProfileName);
        var name = await prompt.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _viewModel.SaveAsAsync(name);
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.LayoutSaveAsFailed(ex.Message));
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_services is null)
        {
            return;
        }

        var window = new SettingsWindow(_services);
        await window.ShowDialog(this);

        // 设置页可能改了屏蔽词、独轮车开关、高能参数或全局弹幕总闸：热更新到正在运行的会话。
        try
        {
            await _services.Danmaku.ReloadSettingsAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(MainWindowStatusText.DanmakuReloadFailed(ex.Message));
            return;
        }

        await _viewModel.ApplyDanmakuMasterSettingAsync();
        _viewModel.SetStatus(MainWindowStatusText.SettingsApplied);
    }

    private void OnClearHighlightEvents(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ClearHighlightEvents();
    }

    private void OnCloseHighlightPanel(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsHighlightPanelVisible = false;
        }
    }

    private void OnGroupFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Avalonia.Controls.Button { Tag: string group })
        {
            return;
        }

        _viewModel.GroupFilter = group;
    }

    private void RefreshGroupPills()
    {
        if (_viewModel is null)
        {
            return;
        }

        GroupPills.Children.Clear();
        foreach (var group in _viewModel.Groups)
        {
            var button = new Avalonia.Controls.Button
            {
                Content = group,
                Tag = group,
                Padding = new Thickness(9, 3),
                // WrapPanel 没有 Spacing，用 Margin 控制换行后的间距。
                Margin = new Thickness(0, 0, 4, 4),
                FontSize = 10,
                CornerRadius = new CornerRadius(12)
            };
            button.Classes.Add("group-pill");
            if (string.Equals(group, _viewModel.GroupFilter, StringComparison.OrdinalIgnoreCase))
            {
                button.Classes.Add("active");
            }

            button.Click += OnGroupFilter;
            GroupPills.Children.Add(button);
        }
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsRecordingCenterVisible == true)
        {
            return;
        }

        _sidebarVisible = !_sidebarVisible;
        ApplyShellLayout();
    }
}
