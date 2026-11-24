using System.Collections.ObjectModel;
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
using LiveMonitor.Platforms;
using LiveMonitor.Platforms.Plugins;
using Button = Avalonia.Controls.Button;
using Color = Avalonia.Media.Color;

namespace LiveMonitor.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services = null!;
    private readonly ObservableCollection<DanmakuBlockKeyword> _keywords = new();
    private readonly WebViewLoginService _loginService = new();

    /// <summary>ASR 语言下拉项与持久化 Id 一一对应（下标相同）。</summary>
    private static readonly string[] AsrLanguageIds = { "auto", "zh", "en", "ja", "ko" };
    private static readonly string[] AsrLanguageNames = { "自动检测", "中文", "英文", "日文", "韩文" };
    private static readonly IBrush AsrNeutralBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#7C8596"));
    private static readonly IBrush AsrSuccessBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#4ADE80"));
    private static readonly IBrush AsrErrorBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F87171"));

    private bool _loading;

    /// <summary>正在把快照回填到 ASR 控件（用于抑制预设下拉的 SelectionChanged 副作用）。</summary>
    private bool _asrLoading;

    /// <summary>ASR 保存/测试进行中（用于禁用按钮并阻止重入）。</summary>
    private bool _asrBusy;

    /// <summary>最近一次 yt-dlp 更新检查结果（供「下载并更新」复用）。</summary>
    private CliUpdateCheckResult? _ytDlpUpdate;

    /// <summary>最近一次平台插件清单检查结果（供「下载并安装」复用）。</summary>
    private PluginUpdateCheckResult? _pluginUpdates;

    /// <summary>组件与插件更新操作进行中（用于禁用按钮并阻止重入）。</summary>
    private bool _updateBusy;

    /// <summary>各平台账号状态文本（按平台索引，登录/清除后统一刷新）。</summary>
    private readonly Dictionary<PlatformType, TextBlock> _accountStatusTexts = new();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(AppServices services) : this()
    {
        _services = services;
        KeywordList.ItemsSource = _keywords;
        AsrPresetBox.ItemsSource = AsrSettingsStore.Presets;
        AsrLanguageBox.ItemsSource = AsrLanguageNames;
        AsrLanguageBox.SelectedIndex = 0;
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
                Foreground = new SolidColorBrush(Color.Parse("#A9B4C6")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var status = new TextBlock
            {
                Text = "未绑定",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#7C8596")),
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
            login.Click += OnPlatformLogin;
            var clear = new Button
            {
                Content = "清除",
                FontSize = 11,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Tag = profile.Platform,
            };
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
            await LoadAsrSettingsAsync();
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

    /// <summary>读取 ASR 快照并回填表单；Key 只回填 Watermark，绝不回显明文。</summary>
    private async Task LoadAsrSettingsAsync()
    {
        _asrLoading = true;
        try
        {
            var snapshot = await _services.AsrSettings.LoadAsync();
            AsrPresetBox.SelectedItem = AsrSettingsStore.ResolvePreset(snapshot.PresetId);
            AsrEndpointBox.Text = snapshot.Endpoint;
            AsrModelBox.Text = snapshot.Model;
            int languageIndex = Array.IndexOf(AsrLanguageIds, snapshot.Language);
            AsrLanguageBox.SelectedIndex = languageIndex >= 0 ? languageIndex : 0;
            UpdateAsrPresetDescription();
            UpdateAsrKeyWatermark(snapshot.HasApiKey);
            SetAsrStatus(
                snapshot.HasApiKey ? "已读取语音识别设置。" : "尚未配置 API Key，请粘贴后点击「保存」。",
                AsrStatusKind.Neutral);
        }
        catch (Exception ex)
        {
            SetAsrStatus($"读取语音识别设置失败：{ex.Message}", AsrStatusKind.Error);
        }
        finally
        {
            _asrLoading = false;
        }
    }

    /// <summary>切换预设时把接口地址与模型填成预设默认值（自定义预设为空，允许手填）。</summary>
    private void OnAsrPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_asrLoading || AsrPresetBox.SelectedItem is not AsrPreset preset)
        {
            return;
        }

        AsrEndpointBox.Text = preset.Endpoint;
        AsrModelBox.Text = preset.Model;
        UpdateAsrPresetDescription();
        if (string.Equals(preset.Id, "custom", StringComparison.OrdinalIgnoreCase))
        {
            SetAsrStatus("自定义服务需要填写接口地址和模型（OpenAI 兼容）。", AsrStatusKind.Neutral);
        }
        else
        {
            SetAsrStatus($"已切换为「{preset.DisplayName}」，点击「保存」后生效。", AsrStatusKind.Neutral);
        }
    }

    private async void OnSaveAsrSettings(object? sender, RoutedEventArgs e)
    {
        if (_asrBusy)
        {
            return;
        }

        var preset = AsrPresetBox.SelectedItem as AsrPreset ?? AsrSettingsStore.ResolvePreset(null);
        var draft = BuildAsrDraft(preset);
        if (RejectInvalidCustomPreset(preset, draft))
        {
            return;
        }

        SetAsrBusy(true, "正在保存语音识别设置…");
        try
        {
            await _services.AsrSettings.SaveAsync(draft);
            // 保存成功后清空明文 Key 并重新读取，刷新 Watermark 与表单内容。
            AsrApiKeyBox.Text = string.Empty;
            await LoadAsrSettingsAsync();
            SetAsrStatus("语音识别设置已保存。", AsrStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetAsrStatus($"保存语音识别设置失败：{ex.Message}", AsrStatusKind.Error);
        }
        finally
        {
            SetAsrBusy(false, null);
        }
    }

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
    {
        if (_asrBusy)
        {
            return;
        }

        var preset = AsrPresetBox.SelectedItem as AsrPreset ?? AsrSettingsStore.ResolvePreset(null);
        var draft = BuildAsrDraft(preset);
        if (RejectInvalidCustomPreset(preset, draft))
        {
            return;
        }

        SetAsrBusy(true, "正在测试连接…");
        AsrTestButton.Content = "正在测试…";
        try
        {
            // 仅用当前表单草稿测试，不写入 AppSettings。
            var result = await _services.AsrSettings.TestConnectionAsync(draft);
            SetAsrStatus(result.Message, result.Success ? AsrStatusKind.Success : AsrStatusKind.Error);
        }
        catch (Exception ex)
        {
            SetAsrStatus($"测试连接失败：{ex.Message}", AsrStatusKind.Error);
        }
        finally
        {
            AsrTestButton.Content = "测试连接";
            SetAsrBusy(false, null);
        }
    }

    /// <summary>把表单内容拼成保存/测试共用草稿；Key 留空表示保留原值。</summary>
    private AsrSettingsDraft BuildAsrDraft(AsrPreset preset)
    {
        string? plainApiKey = string.IsNullOrWhiteSpace(AsrApiKeyBox.Text) ? null : AsrApiKeyBox.Text;
        return new AsrSettingsDraft(
            preset.Id,
            AsrEndpointBox.Text ?? string.Empty,
            AsrModelBox.Text ?? string.Empty,
            GetSelectedAsrLanguageId(),
            plainApiKey);
    }

    /// <summary>自定义服务必须填写接口地址与模型；校验失败时返回 true 并已写状态文本。</summary>
    private bool RejectInvalidCustomPreset(AsrPreset preset, AsrSettingsDraft draft)
    {
        if (!string.Equals(preset.Id, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(draft.Endpoint) && !string.IsNullOrWhiteSpace(draft.Model))
        {
            return false;
        }

        SetAsrStatus("自定义服务需要填写接口地址和模型。", AsrStatusKind.Error);
        return true;
    }

    private string GetSelectedAsrLanguageId()
    {
        int index = AsrLanguageBox.SelectedIndex;
        return index >= 0 && index < AsrLanguageIds.Length
            ? AsrLanguageIds[index]
            : AsrSettingsStore.DefaultLanguage;
    }

    private void UpdateAsrKeyWatermark(bool hasApiKey)
    {
        AsrApiKeyBox.Watermark = hasApiKey ? "已保存，留空保持不变" : "粘贴 API Key";
    }

    private void UpdateAsrPresetDescription()
    {
        if (AsrPresetBox.SelectedItem is AsrPreset preset)
        {
            AsrPresetDescriptionText.Text = preset.Description;
        }
    }

    /// <summary>更新 ASR 分区状态文本并按语气着色（默认灰、成功绿、失败红）。</summary>
    private void SetAsrStatus(string text, AsrStatusKind kind)
    {
        AsrStatusText.Text = text;
        AsrStatusText.Foreground = kind switch
        {
            AsrStatusKind.Success => AsrSuccessBrush,
            AsrStatusKind.Error => AsrErrorBrush,
            _ => AsrNeutralBrush
        };
    }

    /// <summary>进入/退出 ASR 保存或测试：切换两个按钮的可用状态，可附带一条状态提示。</summary>
    private void SetAsrBusy(bool busy, string? statusText)
    {
        _asrBusy = busy;
        AsrSaveButton.IsEnabled = !busy;
        AsrTestButton.IsEnabled = !busy;
        if (statusText is not null)
        {
            SetAsrStatus(statusText, AsrStatusKind.Neutral);
        }
    }

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

    /// <summary>检查 yt-dlp 是否有新版本（只查询、不下载）。</summary>
    private async void OnCheckYtDlpUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _services is null)
        {
            return;
        }

        SetUpdateBusy(true);
        SetUpdateStatus("正在检查 yt-dlp 更新…", UpdateStatusKind.Neutral);
        try
        {
            var result = await _services.CliUpdater.CheckYtDlpAsync();
            _ytDlpUpdate = result;
            if (!result.CheckSucceeded)
            {
                YtDlpVersionText.Text = "检查失败";
                InstallYtDlpButton.IsEnabled = false;
                SetUpdateStatus($"检查 yt-dlp 更新失败：{result.ErrorMessage}", UpdateStatusKind.Error);
                return;
            }

            YtDlpVersionText.Text = $"本地 {result.LocalVersion} · 最新 {result.LatestVersion}";
            InstallYtDlpButton.IsEnabled = result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl);
            SetUpdateStatus(
                result.UpdateAvailable
                    ? $"发现新版本 {result.LatestVersion}，点击「下载并更新」替换 tools 目录内的 yt-dlp.exe。"
                    : "yt-dlp 已是最新版本。",
                result.UpdateAvailable ? UpdateStatusKind.Neutral : UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            InstallYtDlpButton.IsEnabled = false;
            SetUpdateStatus($"检查 yt-dlp 更新失败：{ex.Message}", UpdateStatusKind.Error);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    /// <summary>下载并原子替换 yt-dlp（先校验体积与 MZ 头）。</summary>
    private async void OnInstallYtDlpUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _ytDlpUpdate is null)
        {
            return;
        }

        if (!_ytDlpUpdate.UpdateAvailable || string.IsNullOrWhiteSpace(_ytDlpUpdate.DownloadUrl))
        {
            SetUpdateStatus("没有可用的 yt-dlp 更新，请先点击「检查更新」。", UpdateStatusKind.Error);
            return;
        }

        SetUpdateBusy(true);
        InstallYtDlpButton.IsEnabled = false;
        SetUpdateStatus("正在下载并替换 yt-dlp…", UpdateStatusKind.Neutral);
        try
        {
            var installed = await _services.CliUpdater.DownloadAndInstallAsync(_ytDlpUpdate);
            if (!installed.Success)
            {
                SetUpdateStatus($"更新 yt-dlp 失败：{installed.ErrorMessage}", UpdateStatusKind.Error);
                return;
            }

            YtDlpVersionText.Text = $"本地 {installed.NewVersion} · 已更新";
            _ytDlpUpdate = null;
            SetUpdateStatus($"yt-dlp 已更新到 {installed.NewVersion}。", UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"更新 yt-dlp 失败：{ex.Message}", UpdateStatusKind.Error);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    /// <summary>拉取 providers-manifest.json（主源→镜像）并筛选可用插件更新。</summary>
    private async void OnCheckPluginUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _services is null)
        {
            return;
        }

        SetUpdateBusy(true);
        SetUpdateStatus("正在检查平台插件更新…", UpdateStatusKind.Neutral);
        try
        {
            var result = await _services.PluginUpdates.CheckAsync();
            _pluginUpdates = result;
            if (!result.Succeeded)
            {
                PluginUpdateSummaryText.Text = "检查失败";
                InstallPluginsButton.IsEnabled = false;
                SetUpdateStatus($"检查插件更新失败：{result.ErrorMessage}", UpdateStatusKind.Error);
                return;
            }

            if (result.Updates.Count == 0)
            {
                PluginUpdateSummaryText.Text = $"已是最新（清单来源：{result.ManifestSource}）";
                InstallPluginsButton.IsEnabled = false;
                SetUpdateStatus("没有可用的平台插件更新。", UpdateStatusKind.Success);
                return;
            }

            PluginUpdateSummaryText.Text = string.Join(
                "；",
                result.Updates.Select(u => $"{u.Entry.PluginId} {u.Entry.Version}"));
            InstallPluginsButton.IsEnabled = true;
            SetUpdateStatus(
                $"发现 {result.Updates.Count} 个插件更新，点击「下载并安装」（下载后自动验签）。",
                UpdateStatusKind.Neutral);
        }
        catch (Exception ex)
        {
            InstallPluginsButton.IsEnabled = false;
            SetUpdateStatus($"检查插件更新失败：{ex.Message}", UpdateStatusKind.Error);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    /// <summary>下载并安装插件：验签通过后在无录制/弹幕任务时立即生效。</summary>
    private async void OnInstallPluginUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _pluginUpdates is null || _pluginUpdates.Updates.Count == 0)
        {
            return;
        }

        SetUpdateBusy(true);
        InstallPluginsButton.IsEnabled = false;
        SetUpdateStatus("正在下载并安装平台插件…", UpdateStatusKind.Neutral);
        try
        {
            var result = await _services.PluginUpdates.InstallAsync(_pluginUpdates.Updates);
            if (!result.Succeeded)
            {
                SetUpdateStatus($"安装平台插件失败：{result.ErrorMessage}", UpdateStatusKind.Error);
                return;
            }

            var parts = new List<string>();
            if (result.Installed.Count > 0)
            {
                parts.Add($"已安装 {result.Installed.Count} 个");
            }

            if (result.Applied.Count > 0)
            {
                parts.Add($"已生效 {result.Applied.Count} 个");
            }

            if (result.Deferred.Count > 0)
            {
                parts.Add($"待下次启动生效 {result.Deferred.Count} 个");
            }

            if (result.Failures.Count > 0)
            {
                parts.Add($"失败 {result.Failures.Count} 个：{string.Join("；", result.Failures.Select(f => $"{f.PluginId}（{f.ErrorMessage}）"))}");
            }

            PluginUpdateSummaryText.Text = parts.Count > 0 ? string.Join("，", parts) : "没有变化";
            _pluginUpdates = null;
            SetUpdateStatus(
                parts.Count > 0 ? $"插件更新完成：{string.Join("，", parts)}。" : "没有可安装的插件更新。",
                result.Failures.Count > 0 ? UpdateStatusKind.Error : UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"安装平台插件失败：{ex.Message}", UpdateStatusKind.Error);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        CheckYtDlpButton.IsEnabled = !busy;
        CheckPluginsButton.IsEnabled = !busy;
    }

    private void SetUpdateStatus(string text, UpdateStatusKind kind)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Foreground = kind switch
        {
            UpdateStatusKind.Success => AsrSuccessBrush,
            UpdateStatusKind.Error => AsrErrorBrush,
            _ => AsrNeutralBrush,
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>组件与插件更新分区状态文本的语气（决定前景色）。</summary>
    private enum UpdateStatusKind
    {
        Neutral,
        Success,
        Error
    }

    /// <summary>ASR 分区状态文本的语气（决定前景色）。</summary>
    private enum AsrStatusKind
    {
        Neutral,
        Success,
        Error
    }
}
