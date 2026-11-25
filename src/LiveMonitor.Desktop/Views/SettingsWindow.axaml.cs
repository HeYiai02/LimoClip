using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms;
using Button = Avalonia.Controls.Button;
using Color = Avalonia.Media.Color;

namespace LiveMonitor.Desktop.Views;

/// <summary>
/// 设置窗视图外壳：只承担控件挂载、async void 事件转发、VM 语气到画刷的映射，以及视图/宿主交互。
/// 各分区表单状态与读/存/校验逻辑已迁入 <see cref="AsrSettingsViewModel"/>、
/// <see cref="PluginAndToolUpdateViewModel"/> 与 <see cref="GeneralAndDanmakuSettingsViewModel"/>。
/// <para>
/// 以下逻辑刻意保留在 View（不抽离），因为它们依赖视觉树、临时窗口或宿主时序而非业务状态：
/// 平台账号 WebView2 登录/清除（<see cref="WebViewLoginService"/> 承载，需要临时窗口与 Cookie 轮询）、
/// <see cref="BuildAccountRows"/> 与各平台状态 TextBlock 的动态视觉树操作、设置页导航
/// （<see cref="OnSettingsNav"/>/<see cref="SelectSettingsPage"/>）、窗口打开/关闭。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services = null!;
    private readonly WebViewLoginService _loginService = new();

    /// <summary>ASR 子块表单状态与读/存/测试逻辑（生命周期与窗口一致）。</summary>
    private readonly AsrSettingsViewModel _asrViewModel = null!;

    /// <summary>组件与更新子块状态与更新动作（生命周期与窗口一致）。</summary>
    private readonly PluginAndToolUpdateViewModel _updateViewModel = null!;

    /// <summary>通用与弹幕子块（屏蔽词/弹幕开关/高能参数/Twitch 代理）状态与读/存逻辑（生命周期与窗口一致）。</summary>
    private readonly GeneralAndDanmakuSettingsViewModel _generalViewModel = null!;

    /// <summary>各分区状态文本语气共用的画刷（语气类别由 ViewModel 给出，映射保留在 View）。</summary>
    private static readonly IBrush NeutralStatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#94A3B8"));
    private static readonly IBrush SuccessStatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#22C55E"));
    private static readonly IBrush ErrorStatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#EF4444"));

    /// <summary>左侧设置分组：Key 对应导航按钮 Tag，标题/副标题显示在右侧页头。</summary>
    private static readonly (string Key, string Title, string Subtitle)[] SettingsPages =
    {
        ("account", "账号与平台", "登录态只保存在本机 DPAPI 加密的 PlatformCredentials 中。"),
        ("danmaku", "弹幕与过滤", "屏蔽词、独轮车与监控画面弹幕渲染开关都集中在这里。"),
        ("asr", "语音识别（ASR）", "为剪辑台生成逐字稿；API Key 使用 DPAPI 加密保存。"),
        ("highlight", "高能事件检测", "按短窗弹幕密度与基线倍率自动记录打点。"),
        ("updates", "组件与更新", "yt-dlp 与平台插件的版本检查、下载与验签。"),
    };

    /// <summary>各平台账号状态文本（按平台索引，登录/清除后统一刷新）。</summary>
    private readonly Dictionary<PlatformType, TextBlock> _accountStatusTexts = new();

    public SettingsWindow()
    {
        InitializeComponent();
        SelectSettingsPage("account");
    }

    public SettingsWindow(AppServices services) : this()
    {
        _services = services;
        _asrViewModel = new AsrSettingsViewModel(services.AsrSettings);
        _asrViewModel.PropertyChanged += OnAsrViewModelPropertyChanged;
        _updateViewModel = new PluginAndToolUpdateViewModel(services.CliUpdater, services.PluginUpdates);
        _updateViewModel.PropertyChanged += OnUpdateViewModelPropertyChanged;
        _generalViewModel = new GeneralAndDanmakuSettingsViewModel(services.Settings, services.Blocklist);
        _generalViewModel.PropertyChanged += OnGeneralViewModelPropertyChanged;
        DataContext = _asrViewModel;
        UpdatesPage.DataContext = _updateViewModel;
        // 通用/弹幕子块的绑定源：弹幕页、高能页，以及账号页里的 Twitch 去广告代理输入框；
        // 账号行本身由 BuildAccountRows 手工生成，不依赖该 DataContext。
        DanmakuPage.DataContext = _generalViewModel;
        HighlightPage.DataContext = _generalViewModel;
        AccountPage.DataContext = _generalViewModel;
        BuildAccountRows();
        Opened += OnOpened;
    }

    /// <summary>按平台配置表动态生成账号行，避免每加一个平台就改一遍 XAML（保留在 View：纯视觉树操作）。</summary>
    private void BuildAccountRows()
    {
        AccountRowsPanel.Children.Clear();
        _accountStatusTexts.Clear();

        var first = true;
        foreach (var profile in PlatformLoginProfiles.All)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                Margin = new Thickness(0, first ? 0 : 6, 0, 0),
            };
            first = false;

            var name = new TextBlock
            {
                Text = profile.DisplayName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#F1F5F9")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var status = new TextBlock
            {
                Text = "未绑定",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#64748B")),
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var login = new Button
            {
                Content = "登录 " + profile.DisplayName,
                FontSize = 11,
                Padding = new Thickness(10, 4),
                Tag = profile.Platform,
            };
            login.Classes.Add("subtle-btn");
            login.Click += OnPlatformLogin;
            var clear = new Button
            {
                Content = "清除",
                FontSize = 11,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Tag = profile.Platform,
            };
            clear.Classes.Add("ghost-btn");
            clear.Click += OnPlatformClear;

            Grid.SetColumn(name, 0);
            Grid.SetColumn(status, 1);
            Grid.SetColumn(login, 2);
            Grid.SetColumn(clear, 3);
            row.Children.Add(name);
            row.Children.Add(status);
            row.Children.Add(login);
            row.Children.Add(clear);
            AccountRowsPanel.Children.Add(row);
            _accountStatusTexts[profile.Platform] = status;
        }
    }

    /// <summary>
    /// 窗口打开后加载各分区设置：账号状态属于视觉树交互，保留在 View；
    /// 通用/弹幕与 ASR 分区的设置加载分别交给各自 ViewModel 的 LoadAsync。
    /// </summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await LoadAccountStatusAsync();
            await _generalViewModel.LoadAsync();
            await _asrViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            SetStatusBar($"读取设置失败：{ex.Message}", ErrorStatusBrush);
        }
    }

    private async Task LoadAccountStatusAsync()
    {
        foreach (var profile in PlatformLoginProfiles.All)
        {
            if (!_accountStatusTexts.TryGetValue(profile.Platform, out var statusText))
            {
                continue;
            }

            var cookie = await _services.Credentials.GetCookieAsync(profile.Platform);
            var expired = await _services.Credentials.IsExpiredAsync(profile.Platform);
            statusText.Text = cookie is null
                ? (expired ? "未绑定（上次登录已过期）" : "未绑定")
                : "已绑定（Cookie 已加密保存）";
        }
    }

    /// <summary>ASR 状态语气变化时把 ViewModel 的类别映射为分区状态文本颜色。</summary>
    private void OnAsrViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AsrSettingsViewModel.StatusKind))
        {
            return;
        }

        AsrStatusText.Foreground = _asrViewModel.StatusKind switch
        {
            AsrSettingsStatusKind.Success => SuccessStatusBrush,
            AsrSettingsStatusKind.Error => ErrorStatusBrush,
            _ => NeutralStatusBrush
        };
    }

    /// <summary>更新分区状态语气变化时把 ViewModel 的类别映射为分区状态文本颜色。</summary>
    private void OnUpdateViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginAndToolUpdateViewModel.StatusKind))
        {
            return;
        }

        UpdateStatusText.Foreground = _updateViewModel.StatusKind switch
        {
            UpdateStatusKind.Success => SuccessStatusBrush,
            UpdateStatusKind.Error => ErrorStatusBrush,
            _ => NeutralStatusBrush
        };
    }

    /// <summary>
    /// 通用/弹幕子块状态文案与语气 → 底部共享状态栏：文案随 VM 更新，
    /// 语气到画刷的映射保留在 View（ViewModel 不引用 Avalonia 画刷）。
    /// </summary>
    private void OnGeneralViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GeneralAndDanmakuSettingsViewModel.StatusText):
                StatusText.Text = _generalViewModel.StatusText;
                break;
            case nameof(GeneralAndDanmakuSettingsViewModel.StatusKind):
                StatusText.Foreground = _generalViewModel.StatusKind switch
                {
                    GeneralAndDanmakuStatusKind.Success => SuccessStatusBrush,
                    GeneralAndDanmakuStatusKind.Error => ErrorStatusBrush,
                    _ => NeutralStatusBrush
                };
                break;
        }
    }

    private async void OnSaveAsrSettings(object? sender, RoutedEventArgs e)
        => await _asrViewModel.SaveAsync();

    private async void OnTestAsrConnection(object? sender, RoutedEventArgs e)
        => await _asrViewModel.TestConnectionAsync();

    // ---- 通用与弹幕子块的事件转发（状态与读/存逻辑在 ViewModel）。----

    private async void OnAddKeyword(object? sender, RoutedEventArgs e)
        => await _generalViewModel.AddKeywordAsync();

    private async void OnRemoveKeyword(object? sender, RoutedEventArgs e)
        => await _generalViewModel.RemoveKeywordAsync();

    private async void OnSaveHighlight(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SaveHighlightAsync();

    private async void OnResetHighlight(object? sender, RoutedEventArgs e)
        => await _generalViewModel.ResetHighlightAsync();

    private async void OnSaveTwitchProxy(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SaveTwitchProxyAsync();

    private async void OnUnicycleChanged(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SetUnicycleEnabledAsync(UnicycleToggle.IsChecked == true);

    private async void OnDanmakuPreviewChanged(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SetDanmakuPreviewEnabledAsync(DanmakuPreviewToggle.IsChecked == true);

    private async void OnDanmakuStickerChanged(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SetDanmakuStickerVisibleAsync(DanmakuStickerToggle.IsChecked == true);

    private async void OnDanmakuEmoteImagesChanged(object? sender, RoutedEventArgs e)
        => await _generalViewModel.SetDanmakuEmoteImagesVisibleAsync(DanmakuEmoteImagesToggle.IsChecked == true);

    // ---- 平台账号登录/清除：保留在 View（WebView2 临时窗口与 Cookie 轮询属宿主交互）。----

    private async void OnPlatformLogin(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlatformType platform })
        {
            await LoginAsync(platform);
        }
    }

    private async Task LoginAsync(PlatformType platform)
    {
        var displayName = WebViewLoginService.DisplayName(platform);
        SetStatusBar($"正在打开 {displayName} 登录窗…", NeutralStatusBrush);
        try
        {
            var cookie = await _loginService.LoginAsync(platform);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                SetStatusBar("登录窗已关闭，未捕获到登录 Cookie。", NeutralStatusBrush);
                return;
            }

            await _services.Credentials.SaveAsync(platform, null, cookie);
            await LoadAccountStatusAsync();
            SetStatusBar($"{displayName} 登录态已保存（DPAPI）。", NeutralStatusBrush);
        }
        catch (Exception ex)
        {
            SetStatusBar($"登录失败：{ex.Message}", NeutralStatusBrush);
        }
    }

    private async void OnPlatformClear(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlatformType platform })
        {
            await ClearAsync(platform);
        }
    }

    private async Task ClearAsync(PlatformType platform)
    {
        try
        {
            await _services.Credentials.RemoveAsync(platform);
            await LoadAccountStatusAsync();
            SetStatusBar($"{WebViewLoginService.DisplayName(platform)} 登录态已清除。", NeutralStatusBrush);
        }
        catch (Exception ex)
        {
            SetStatusBar($"清除失败：{ex.Message}", NeutralStatusBrush);
        }
    }

    /// <summary>「检查更新」/「检查插件更新」共用转发入口（按 sender 区分目标，更新逻辑在 ViewModel）。</summary>
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, CheckYtDlpButton))
        {
            await _updateViewModel.CheckYtDlpAsync();
        }
        else if (ReferenceEquals(sender, CheckPluginsButton))
        {
            await _updateViewModel.CheckPluginUpdatesAsync();
        }
    }

    /// <summary>「下载并更新」/「下载并安装」共用转发入口（按 sender 区分目标，更新逻辑在 ViewModel）。</summary>
    private async void OnInstallUpdates(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, InstallYtDlpButton))
        {
            await _updateViewModel.InstallYtDlpAsync();
        }
        else if (ReferenceEquals(sender, InstallPluginsButton))
        {
            await _updateViewModel.InstallPluginUpdatesAsync();
        }
    }

    /// <summary>底部状态栏：输入文案并把前景色设为对应语气画刷（语气类别由各 ViewModel 给出）。</summary>
    private void SetStatusBar(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnSettingsNav(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            SelectSettingsPage(key);
        }
    }

    /// <summary>切换左侧设置分组：只切换可见性，不重建控件（保留各页滚动位置与表单状态）。</summary>
    private void SelectSettingsPage(string key)
    {
        var pages = new (string Key, Button Nav, ScrollViewer Page)[]
        {
            ("account", NavAccount, AccountPage),
            ("danmaku", NavDanmaku, DanmakuPage),
            ("asr", NavAsr, AsrPage),
            ("highlight", NavHighlight, HighlightPage),
            ("updates", NavUpdates, UpdatesPage),
        };

        foreach (var (pageKey, nav, page) in pages)
        {
            bool active = string.Equals(pageKey, key, StringComparison.Ordinal);
            nav.Classes.Set("active", active);
            page.IsVisible = active;
        }

        foreach (var (pageKey, title, subtitle) in SettingsPages)
        {
            if (!string.Equals(pageKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            PageTitle.Text = title;
            PageSubtitle.Text = subtitle;
            break;
        }
    }
}
