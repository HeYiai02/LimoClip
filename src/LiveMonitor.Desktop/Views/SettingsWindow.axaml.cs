using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms;
using Button = Avalonia.Controls.Button;
using Color = Avalonia.Media.Color;

namespace LiveMonitor.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services = null!;
    private readonly ObservableCollection<DanmakuBlockKeyword> _keywords = new();
    private readonly WebViewLoginService _loginService = new();

    /// <summary>ASR 子块表单状态与读/存/测试逻辑（生命周期与窗口一致）。</summary>
    private readonly AsrSettingsViewModel _asrViewModel = null!;

    /// <summary>组件与更新子块状态与更新动作（生命周期与窗口一致）。</summary>
    private readonly PluginAndToolUpdateViewModel _updateViewModel = null!;

    /// <summary>更新分区与 ASR 分区共用的状态文本语气画刷（语气类别由 ViewModel 给出）。</summary>
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

    private bool _loading;

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
        DataContext = _asrViewModel;
        UpdatesPage.DataContext = _updateViewModel;
        KeywordList.ItemsSource = _keywords;
        BuildAccountRows();
        Opened += OnOpened;
    }

    /// <summary>按平台配置表动态生成账号行，避免每加一个平台就改一遍 XAML。</summary>
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

    private async void OnOpened(object? sender, EventArgs e)
    {
        _loading = true;
        try
        {
            await LoadAccountStatusAsync();
            await ReloadKeywordsAsync();
            var value = await _services.Settings.GetAsync(AppSettingKeys.BlockUnicycleEnabled);
            UnicycleToggle.IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

            var preview = await _services.Settings.GetAsync(AppSettingKeys.DanmakuPreviewEnabled);
            DanmakuPreviewToggle.IsChecked = string.Equals(preview, "true", StringComparison.OrdinalIgnoreCase);

            var sticker = await _services.Settings.GetAsync(AppSettingKeys.DanmakuStickerVisible);
            // 键缺失时默认开启渲染（与代码默认值保持一致）。
            DanmakuStickerToggle.IsChecked = string.IsNullOrWhiteSpace(sticker)
                || !string.Equals(sticker, "false", StringComparison.OrdinalIgnoreCase);

            var emoteImages = await _services.Settings.GetAsync(AppSettingKeys.DanmakuEmoteImagesVisible);
            // 键缺失时默认开启（与 UI 代码默认值保持一致）；只有显式 false 才关闭。
            DanmakuEmoteImagesToggle.IsChecked = string.IsNullOrWhiteSpace(emoteImages)
                || !string.Equals(emoteImages, "false", StringComparison.OrdinalIgnoreCase);

            await LoadHighlightSettingsAsync();
            await _asrViewModel.LoadAsync();
            await LoadTwitchProxyAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取设置失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
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

    private async Task ReloadKeywordsAsync()
    {
        var keywords = await _services.Blocklist.GetAllAsync();
        _keywords.Clear();
        foreach (var keyword in keywords)
        {
            _keywords.Add(keyword);
        }
    }

    private async Task LoadHighlightSettingsAsync()
    {
        var options = await HighlightSettingsStore.LoadAsync(_services.Settings);
        HighlightShortWindow.Value = options.ShortWindowSeconds;
        HighlightBaselineWindow.Value = options.BaselineWindowSeconds;
        HighlightDensityMultiplier.Value = (decimal)options.DensityMultiplier;
        HighlightMinShortWindowCount.Value = options.MinShortWindowCount;
        HighlightCooldownSeconds.Value = options.CooldownSeconds;
        HighlightBoostKeywordsBox.Text = HighlightSettingsStore.FormatBoostKeywords(options.BoostKeywords);
    }

    private HighlightRuleOptions ReadHighlightOptions()
    {
        var defaults = HighlightRuleOptions.Default;
        return new HighlightRuleOptions
        {
            ShortWindowSeconds = (int)(HighlightShortWindow.Value ?? defaults.ShortWindowSeconds),
            BaselineWindowSeconds = (int)(HighlightBaselineWindow.Value ?? defaults.BaselineWindowSeconds),
            DensityMultiplier = (double)(HighlightDensityMultiplier.Value ?? (decimal)defaults.DensityMultiplier),
            MinShortWindowCount = (int)(HighlightMinShortWindowCount.Value ?? defaults.MinShortWindowCount),
            CooldownSeconds = (int)(HighlightCooldownSeconds.Value ?? defaults.CooldownSeconds),
            BoostKeywords = HighlightSettingsStore.ParseBoostKeywords(HighlightBoostKeywordsBox.Text)
        };
    }

    private async void OnSaveHighlight(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HighlightSettingsStore.SaveAsync(_services.Settings, ReadHighlightOptions());
            await LoadHighlightSettingsAsync();
            StatusText.Text = "高能参数已保存（对运行中的会话即时生效）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存高能参数失败：{ex.Message}";
        }
    }

    private async void OnResetHighlight(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HighlightSettingsStore.SaveAsync(_services.Settings, HighlightRuleOptions.Default);
            await LoadHighlightSettingsAsync();
            StatusText.Text = "高能参数已恢复默认值。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"恢复默认值失败：{ex.Message}";
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

    private async void OnSaveAsrSettings(object? sender, RoutedEventArgs e)
        => await _asrViewModel.SaveAsync();

    /// <summary>读取 Twitch 去广告代理模板（V0.5-G）；留空表示不使用外部代理。</summary>
    private async Task LoadTwitchProxyAsync()
    {
        var template = await _services.Settings.GetAsync(AppSettingKeys.TwitchPlaylistProxyUrl);
        TwitchProxyBox.Text = template ?? string.Empty;
        ApplyTwitchProxyTemplate(template);
    }

    private async void OnSaveTwitchProxy(object? sender, RoutedEventArgs e)
    {
        var template = TwitchProxyBox.Text?.Trim() ?? string.Empty;
        try
        {
            await _services.Settings.SetAsync(AppSettingKeys.TwitchPlaylistProxyUrl, template);
            ApplyTwitchProxyTemplate(template);
            StatusText.Text = string.IsNullOrWhiteSpace(template)
                ? "已保存：Twitch 仅使用内置原生播放列表（GQL + Usher）。"
                : "已保存：Twitch 将优先使用自定义去广告代理。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存 Twitch 代理失败：{ex.Message}";
        }
    }

    private static void ApplyTwitchProxyTemplate(string? template)
    {
        var normalized = string.IsNullOrWhiteSpace(template) ? null : template.Trim();
        TwitchAdConfig.PlaylistProxyTemplateProvider = () => normalized;
    }

    private async void OnTestAsrConnection(object? sender, RoutedEventArgs e)
        => await _asrViewModel.TestConnectionAsync();

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
        StatusText.Text = $"正在打开 {displayName} 登录窗…";
        try
        {
            var cookie = await _loginService.LoginAsync(platform);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                StatusText.Text = "登录窗已关闭，未捕获到登录 Cookie。";
                return;
            }

            await _services.Credentials.SaveAsync(platform, null, cookie);
            await LoadAccountStatusAsync();
            StatusText.Text = $"{displayName} 登录态已保存（DPAPI）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"登录失败：{ex.Message}";
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
            StatusText.Text = $"{WebViewLoginService.DisplayName(platform)} 登录态已清除。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除失败：{ex.Message}";
        }
    }

    private async void OnAddKeyword(object? sender, RoutedEventArgs e)
    {
        var text = KeywordBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            if (await _services.Blocklist.ExistsAsync(text))
            {
                StatusText.Text = "这个词已经在屏蔽词库中。";
                return;
            }

            var isRegex = KeywordIsRegexCheck.IsChecked == true;
            await _services.Blocklist.AddAsync(new DanmakuBlockKeyword
            {
                Id = Guid.NewGuid(),
                Keyword = text,
                IsRegex = isRegex,
                CreatedAt = DateTime.UtcNow
            });
            KeywordBox.Text = string.Empty;
            KeywordIsRegexCheck.IsChecked = false;
            await ReloadKeywordsAsync();
            StatusText.Text = isRegex ? "已添加正则屏蔽词。" : "已添加屏蔽词。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"添加失败：{ex.Message}";
        }
    }

    private async void OnRemoveKeyword(object? sender, RoutedEventArgs e)
    {
        if (KeywordList.SelectedItem is not DanmakuBlockKeyword keyword)
        {
            StatusText.Text = "请先选中一个屏蔽词。";
            return;
        }

        try
        {
            await _services.Blocklist.DeleteAsync(keyword.Id);
            await ReloadKeywordsAsync();
            StatusText.Text = "已删除屏蔽词。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ex.Message}";
        }
    }

    private async void OnUnicycleChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        try
        {
            await _services.Settings.SetAsync(
                AppSettingKeys.BlockUnicycleEnabled,
                UnicycleToggle.IsChecked == true ? "true" : "false");
            StatusText.Text = UnicycleToggle.IsChecked == true
                ? "独轮车自动屏蔽已开启。"
                : "独轮车自动屏蔽已关闭。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存开关失败：{ex.Message}";
        }
    }

    private async void OnDanmakuPreviewChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        try
        {
            await _services.Settings.SetAsync(
                AppSettingKeys.DanmakuPreviewEnabled,
                DanmakuPreviewToggle.IsChecked == true ? "true" : "false");
            StatusText.Text = DanmakuPreviewToggle.IsChecked == true
                ? "监控画面弹幕预览已开启。"
                : "监控画面弹幕预览已关闭。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存弹幕预览开关失败：{ex.Message}";
        }
    }

    private async void OnDanmakuStickerChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        try
        {
            await _services.Settings.SetAsync(
                AppSettingKeys.DanmakuStickerVisible,
                DanmakuStickerToggle.IsChecked == true ? "true" : "false");
            StatusText.Text = DanmakuStickerToggle.IsChecked == true
                ? "纯表情包会渲染为 [表情名] 文本。"
                : "纯表情包仅参与统计与高能检测，不再渲染。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存表情包渲染开关失败：{ex.Message}";
        }
    }

    private async void OnDanmakuEmoteImagesChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        try
        {
            await _services.Settings.SetAsync(
                AppSettingKeys.DanmakuEmoteImagesVisible,
                DanmakuEmoteImagesToggle.IsChecked == true ? "true" : "false");
            StatusText.Text = DanmakuEmoteImagesToggle.IsChecked == true
                ? "表情图片已开启（仅渲染静态帧，未加载成功的表情先显示 [表情名]）。"
                : "表情图片已关闭，表情按 [表情] 文本显示。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存表情图片开关失败：{ex.Message}";
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
