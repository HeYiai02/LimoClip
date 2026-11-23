using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(AppServices services) : this()
    {
        _services = services;
        KeywordList.ItemsSource = _keywords;
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

            await LoadHighlightSettingsAsync();
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
