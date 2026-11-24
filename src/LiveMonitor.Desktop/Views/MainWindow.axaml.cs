using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Core;
using Dock.Settings;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

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
    private DateTime _lastFullscreenToggleAt;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        Dock.SizeChanged += OnDockSizeChanged;
        Dock.AddHandler(InputElement.PointerPressedEvent, OnDockPointerPressed, RoutingStrategies.Tunnel);
        RoomList.AddHandler(InputElement.PointerPressedEvent, OnRoomPointerPressed, RoutingStrategies.Tunnel);
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
                    Title = "选择录像保存目录",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };
            _viewModel.PickClipVideoFileAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择本地视频文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("视频文件 (mkv / mp4)")
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
                    Title = "选择切片导出目录",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };
            DataContext = _viewModel;
            _viewModel.ProportionsRestored += (_, _) => SyncVisualProportions();
            _viewModel.Groups.CollectionChanged += (_, _) => RefreshGroupPills();
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.Layout))
                {
                    Dock.Layout = _viewModel.Layout;
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
            };
            await _viewModel.InitializeAsync();
            RefreshGroupPills();
            _viewModel.Metrics.Start();
            _viewModel.SetWorkspaceAspect(Dock.Bounds.Width, Dock.Bounds.Height);
            Dock.Layout = _viewModel.Layout;
            EnsureSplitterTemplate();
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

            var dialog = new StartupErrorWindow("启动失败", details);
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

        _viewModel?.Dispose();
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
        Classes.Set("immersive-idle", false);
        SetPlayerControlBarsVisible(true);
        if (_isDesktopFullscreen)
        {
            DesktopFullscreenHint.IsVisible = true;
        }
    }

    private void HideImmersiveControls()
    {
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
        var players = Dock.GetVisualDescendants().OfType<Player.MpvPlayerView>().ToList();
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
        _viewModel.SetStatus("已进入桌面全屏：F11 或 Esc 退出。");
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
        _viewModel?.SetStatus("已退出桌面全屏。");
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
            _viewModel.SetStatus($"切换主副屏布局失败：{ex.Message}");
        }
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
                _viewModel.SetStatus($"修改失败：{ex.Message}");
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
            _viewModel.SetStatus($"删除失败：{ex.Message}");
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
            _viewModel.SetStatus($"保存失败：{ex.Message}");
        }
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var prompt = new TextPromptWindow("方案名称", _viewModel.SelectedProfile?.Name ?? "新方案");
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
            _viewModel.SetStatus($"另存失败：{ex.Message}");
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
            _viewModel.SetStatus($"弹幕设置热更新失败：{ex.Message}");
            return;
        }

        await _viewModel.ApplyDanmakuMasterSettingAsync();
        _viewModel.SetStatus("设置已应用。");
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
