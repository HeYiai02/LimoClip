using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using LiveMonitor.Desktop.Services;

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
        Opened += OnOpened;
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
        var bilibili = await _services.Credentials.GetCookieAsync(PlatformType.Bilibili);
        var twitch = await _services.Credentials.GetCookieAsync(PlatformType.Twitch);
        var bilibiliExpired = await _services.Credentials.IsExpiredAsync(PlatformType.Bilibili);
        var twitchExpired = await _services.Credentials.IsExpiredAsync(PlatformType.Twitch);

        BilibiliStatus.Text = bilibili is null
            ? (bilibiliExpired ? "未绑定（上次登录已过期）" : "未绑定")
            : "已绑定（Cookie 已加密保存）";
        TwitchStatus.Text = twitch is null
            ? (twitchExpired ? "未绑定（上次登录已过期）" : "未绑定")
            : "已绑定（Cookie 已加密保存）";
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

    private async void OnBilibiliLogin(object? sender, RoutedEventArgs e)
    {
        await LoginAsync(PlatformType.Bilibili);
    }

    private async void OnTwitchLogin(object? sender, RoutedEventArgs e)
    {
        await LoginAsync(PlatformType.Twitch);
    }

    private async Task LoginAsync(PlatformType platform)
    {
        StatusText.Text = platform == PlatformType.Bilibili ? "正在打开 B 站登录窗…" : "正在打开 Twitch 登录窗…";
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
            StatusText.Text = platform == PlatformType.Bilibili
                ? "B 站登录态已保存（DPAPI）。"
                : "Twitch 登录态已保存（DPAPI）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"登录失败：{ex.Message}";
        }
    }

    private async void OnClearBilibili(object? sender, RoutedEventArgs e)
    {
        await ClearAsync(PlatformType.Bilibili);
    }

    private async void OnClearTwitch(object? sender, RoutedEventArgs e)
    {
        await ClearAsync(PlatformType.Twitch);
    }

    private async Task ClearAsync(PlatformType platform)
    {
        try
        {
            await _services.Credentials.RemoveAsync(platform);
            await LoadAccountStatusAsync();
            StatusText.Text = "登录态已清除。";
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>ASR 分区状态文本的语气（决定前景色）。</summary>
    private enum AsrStatusKind
    {
        Neutral,
        Success,
        Error
    }
}
