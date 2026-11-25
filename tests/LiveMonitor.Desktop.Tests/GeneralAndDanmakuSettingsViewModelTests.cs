using System.Globalization;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 设置页「通用与弹幕」子块 ViewModel 的无 UI 单测：用内存版设置仓储、屏蔽词仓储与捕获委托，
/// 覆盖加载回填（含旧版键缺省语义）、屏蔽词增删、高能参数保存/重置、Twitch 去广告代理、
/// 四个弹幕开关的持久化与失败/忙态分支。不启动 UI、不访问网络。
/// 无法在此覆盖、需真机冒烟验证的部分：AXAML 控件绑定是否生效、开关 TwoWay 绑定与
/// Checked/Unchecked 事件的实际触发顺序、状态文本画刷（View 按 StatusKind 映射）、
/// 页面级 DataContext 的挂载时机。
/// </summary>
public sealed class GeneralAndDanmakuSettingsViewModelTests
{
    [Fact]
    public void Initial_state_is_idle_and_neutral()
    {
        var (vm, _, _, applied) = CreateViewModel();

        Assert.Empty(vm.Keywords);
        Assert.Null(vm.SelectedKeyword);
        Assert.Equal(string.Empty, vm.NewKeywordText);
        Assert.False(vm.NewKeywordIsRegex);
        Assert.False(vm.UnicycleEnabled);
        Assert.False(vm.DanmakuPreviewEnabled);
        Assert.False(vm.DanmakuStickerVisible);
        Assert.False(vm.DanmakuEmoteImagesVisible);
        Assert.Equal(string.Empty, vm.TwitchProxyTemplate);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsNotBusy);
        Assert.False(vm.IsLoading);
        Assert.Empty(applied);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var settings = new TestAppSettingsRepository();
        var blocklist = new TestBlocklistRepository();

        Assert.Throws<ArgumentNullException>(
            () => new GeneralAndDanmakuSettingsViewModel(null!, blocklist));
        Assert.Throws<ArgumentNullException>(
            () => new GeneralAndDanmakuSettingsViewModel(settings, null!));
        Assert.Throws<ArgumentNullException>(
            () => new GeneralAndDanmakuSettingsViewModel(settings, blocklist, null!));
    }

    [Fact]
    public async Task LoadAsync_populates_keywords_switches_highlight_and_twitch()
    {
        var settings = new TestAppSettingsRepository();
        settings.Values[AppSettingKeys.BlockUnicycleEnabled] = "true";
        settings.Values[AppSettingKeys.DanmakuPreviewEnabled] = "false";
        settings.Values[AppSettingKeys.DanmakuStickerVisible] = "false";
        settings.Values[AppSettingKeys.DanmakuEmoteImagesVisible] = "true";
        settings.Values[AppSettingKeys.HighlightShortWindowSeconds] = "30";
        settings.Values[AppSettingKeys.HighlightBaselineWindowSeconds] = "120";
        settings.Values[AppSettingKeys.HighlightDensityMultiplier] = "2.5";
        settings.Values[AppSettingKeys.HighlightMinShortWindowCount] = "8";
        settings.Values[AppSettingKeys.HighlightCooldownSeconds] = "15";
        settings.Values[AppSettingKeys.HighlightBoostKeywords] = "666\n草";
        settings.Values[AppSettingKeys.TwitchPlaylistProxyUrl] = " https://proxy.example/live.m3u8 ";
        var blocklist = new TestBlocklistRepository();
        blocklist.Seed("广告", isRegex: false);
        blocklist.Seed("^抽奖", isRegex: true);
        var applied = new List<string?>();
        var vm = new GeneralAndDanmakuSettingsViewModel(settings, blocklist, applied.Add);

        await vm.LoadAsync();

        Assert.Equal(2, vm.Keywords.Count);
        Assert.Equal("广告", vm.Keywords[0].Keyword);
        Assert.True(vm.Keywords[1].IsRegex);
        Assert.True(vm.UnicycleEnabled);
        Assert.False(vm.DanmakuPreviewEnabled);
        Assert.False(vm.DanmakuStickerVisible);
        Assert.True(vm.DanmakuEmoteImagesVisible);
        Assert.Equal(30m, vm.HighlightShortWindow);
        Assert.Equal(120m, vm.HighlightBaselineWindow);
        Assert.Equal(2.5m, vm.HighlightDensityMultiplier);
        Assert.Equal(8m, vm.HighlightMinShortWindowCount);
        Assert.Equal(15m, vm.HighlightCooldownSeconds);
        Assert.Equal("666\n草", vm.HighlightBoostKeywords);
        Assert.Equal(" https://proxy.example/live.m3u8 ", vm.TwitchProxyTemplate);
        Assert.Equal(new string?[] { "https://proxy.example/live.m3u8" }, applied);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_applies_legacy_defaults_for_missing_switches()
    {
        var (vm, _, _, applied) = CreateViewModel();

        await vm.LoadAsync();

        // 独轮车/预览：只有显式 true 才开启；表情包/表情图片：缺失视为开启。
        Assert.False(vm.UnicycleEnabled);
        Assert.False(vm.DanmakuPreviewEnabled);
        Assert.True(vm.DanmakuStickerVisible);
        Assert.True(vm.DanmakuEmoteImagesVisible);
        Assert.Equal(HighlightRuleOptions.DefaultShortWindowSeconds, (int)vm.HighlightShortWindow!.Value);
        Assert.Equal(HighlightRuleOptions.DefaultBaselineWindowSeconds, (int)vm.HighlightBaselineWindow!.Value);
        Assert.Equal((decimal)HighlightRuleOptions.DefaultDensityMultiplier, vm.HighlightDensityMultiplier);
        Assert.Equal(HighlightRuleOptions.DefaultMinShortWindowCount, (int)vm.HighlightMinShortWindowCount!.Value);
        Assert.Equal(HighlightRuleOptions.DefaultCooldownSeconds, (int)vm.HighlightCooldownSeconds!.Value);
        Assert.Equal("666\n草\n神回\nPog", vm.HighlightBoostKeywords);
        Assert.Equal(string.Empty, vm.TwitchProxyTemplate);
        // 缺失的代理键按「不使用外部代理」应用。
        Assert.Equal(new string?[] { null }, applied);
    }

    [Fact]
    public async Task LoadAsync_parses_switches_by_legacy_rules()
    {
        var settings = new TestAppSettingsRepository();
        settings.Values[AppSettingKeys.BlockUnicycleEnabled] = "TRUE";
        settings.Values[AppSettingKeys.DanmakuPreviewEnabled] = "1";
        settings.Values[AppSettingKeys.DanmakuStickerVisible] = "0";
        settings.Values[AppSettingKeys.DanmakuEmoteImagesVisible] = "FALSE";
        var blocklist = new TestBlocklistRepository();
        var applied = new List<string?>();
        var vm = new GeneralAndDanmakuSettingsViewModel(settings, blocklist, applied.Add);

        await vm.LoadAsync();

        Assert.True(vm.UnicycleEnabled); // 忽略大小写的 true
        Assert.False(vm.DanmakuPreviewEnabled); // 非 true 一律关闭
        Assert.True(vm.DanmakuStickerVisible); // 只有显式 false 才关闭
        Assert.False(vm.DanmakuEmoteImagesVisible); // 忽略大小写的 false
    }

    [Fact]
    public async Task LoadAsync_blank_proxy_key_clears_template_and_applies_null()
    {
        var settings = new TestAppSettingsRepository();
        settings.Values[AppSettingKeys.TwitchPlaylistProxyUrl] = "   ";
        var blocklist = new TestBlocklistRepository();
        var applied = new List<string?>();
        var vm = new GeneralAndDanmakuSettingsViewModel(settings, blocklist, applied.Add);

        await vm.LoadAsync();

        Assert.Equal("   ", vm.TwitchProxyTemplate);
        Assert.Equal(new string?[] { null }, applied);
    }

    [Fact]
    public async Task LoadAsync_reports_error_and_resets_loading_state()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        blocklist.GetAllException = new InvalidOperationException("db down");

        await vm.LoadAsync();

        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("读取设置失败", vm.StatusText);
        Assert.Contains("db down", vm.StatusText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_sets_loading_state_while_repository_is_pending()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        blocklist.GetAllRelease = NewSignal();

        var task = vm.LoadAsync();
        await blocklist.GetAllEntered;

        Assert.True(vm.IsLoading);
        Assert.Equal(string.Empty, vm.StatusText);

        blocklist.GetAllRelease!.SetResult();
        await task;

        Assert.False(vm.IsLoading);
        Assert.True(vm.DanmakuStickerVisible);
    }

    [Fact]
    public async Task AddKeywordAsync_ignores_blank_input()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        vm.NewKeywordText = "   ";
        vm.NewKeywordIsRegex = true;

        await vm.AddKeywordAsync();

        Assert.Equal(0, blocklist.AddAttempts);
        Assert.Empty(vm.Keywords);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AddKeywordAsync_adds_trimmed_keyword_and_refreshes_list()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        vm.NewKeywordText = " 广告 ";

        await vm.AddKeywordAsync();

        var saved = Assert.Single(blocklist.Keywords);
        Assert.Equal("广告", saved.Keyword);
        Assert.False(saved.IsRegex);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal("广告", Assert.Single(vm.Keywords).Keyword);
        Assert.Equal(string.Empty, vm.NewKeywordText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("已添加屏蔽词。", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AddKeywordAsync_adds_regex_keyword_and_resets_checkbox()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        vm.NewKeywordText = "^抽奖";
        vm.NewKeywordIsRegex = true;

        await vm.AddKeywordAsync();

        Assert.True(Assert.Single(blocklist.Keywords).IsRegex);
        Assert.False(vm.NewKeywordIsRegex);
        Assert.Equal("已添加正则屏蔽词。", vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task AddKeywordAsync_duplicate_reports_neutral_without_persisting()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        blocklist.ExistsOverride = _ => true;
        vm.NewKeywordText = "广告";

        await vm.AddKeywordAsync();

        Assert.Equal(0, blocklist.AddAttempts);
        Assert.Equal(GeneralAndDanmakuStatusKind.Neutral, vm.StatusKind);
        Assert.Equal("这个词已经在屏蔽词库中。", vm.StatusText);
        Assert.Equal("广告", vm.NewKeywordText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AddKeywordAsync_reports_failure_and_keeps_input()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        blocklist.AddException = new InvalidOperationException("locked");
        vm.NewKeywordText = "广告";

        await vm.AddKeywordAsync();

        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("添加失败", vm.StatusText);
        Assert.Contains("locked", vm.StatusText);
        Assert.Equal("广告", vm.NewKeywordText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveKeywordAsync_without_selection_prompts()
    {
        var (vm, _, blocklist, _) = CreateViewModel();

        await vm.RemoveKeywordAsync();

        Assert.Equal(0, blocklist.DeleteAttempts);
        Assert.Equal(GeneralAndDanmakuStatusKind.Neutral, vm.StatusKind);
        Assert.Equal("请先选中一个屏蔽词。", vm.StatusText);
    }

    [Fact]
    public async Task RemoveKeywordAsync_deletes_selected_and_refreshes_list()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        var seeded = blocklist.Seed("广告", isRegex: false);
        await vm.LoadAsync();
        vm.SelectedKeyword = Assert.Single(vm.Keywords);

        await vm.RemoveKeywordAsync();

        Assert.Equal(1, blocklist.DeleteAttempts);
        Assert.Equal(seeded.Id, blocklist.LastDeletedId);
        Assert.Empty(blocklist.Keywords);
        Assert.Empty(vm.Keywords);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("已删除屏蔽词。", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveKeywordAsync_reports_failure()
    {
        var (vm, _, blocklist, _) = CreateViewModel();
        blocklist.Seed("广告", isRegex: false);
        await vm.LoadAsync();
        vm.SelectedKeyword = Assert.Single(vm.Keywords);
        blocklist.DeleteException = new InvalidOperationException("locked");

        await vm.RemoveKeywordAsync();

        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("删除失败", vm.StatusText);
        Assert.Contains("locked", vm.StatusText);
        Assert.Single(vm.Keywords);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveHighlightAsync_persists_normalized_form_values()
    {
        var (vm, settings, _, _) = CreateViewModel();
        vm.HighlightShortWindow = 30m;
        vm.HighlightBaselineWindow = 120m;
        vm.HighlightDensityMultiplier = 4.5m;
        vm.HighlightMinShortWindowCount = 20m;
        vm.HighlightCooldownSeconds = 10m;
        vm.HighlightBoostKeywords = "666\n草\n草\nPog";

        await vm.SaveHighlightAsync();

        Assert.Equal("30", settings.Values[AppSettingKeys.HighlightShortWindowSeconds]);
        Assert.Equal("120", settings.Values[AppSettingKeys.HighlightBaselineWindowSeconds]);
        Assert.Equal("4.5", settings.Values[AppSettingKeys.HighlightDensityMultiplier]);
        Assert.Equal("20", settings.Values[AppSettingKeys.HighlightMinShortWindowCount]);
        Assert.Equal("10", settings.Values[AppSettingKeys.HighlightCooldownSeconds]);
        Assert.Equal("666\n草\nPog", settings.Values[AppSettingKeys.HighlightBoostKeywords]);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("高能参数已保存（对运行中的会话即时生效）。", vm.StatusText);
        Assert.False(vm.IsBusy);
        // 保存后重新读取回填，关键词按存储格式（去重）刷新。
        Assert.Equal(30m, vm.HighlightShortWindow);
        Assert.Equal("666\n草\nPog", vm.HighlightBoostKeywords);
    }

    [Fact]
    public async Task SaveHighlightAsync_uses_defaults_for_blank_numbers()
    {
        var (vm, settings, _, _) = CreateViewModel();
        vm.HighlightShortWindow = null;
        vm.HighlightBaselineWindow = null;
        vm.HighlightDensityMultiplier = null;
        vm.HighlightMinShortWindowCount = null;
        vm.HighlightCooldownSeconds = null;
        vm.HighlightBoostKeywords = string.Empty;

        await vm.SaveHighlightAsync();

        Assert.Equal(
            HighlightRuleOptions.DefaultShortWindowSeconds.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightShortWindowSeconds]);
        Assert.Equal(
            HighlightRuleOptions.DefaultBaselineWindowSeconds.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightBaselineWindowSeconds]);
        Assert.Equal(
            HighlightRuleOptions.DefaultDensityMultiplier,
            double.Parse(settings.Values[AppSettingKeys.HighlightDensityMultiplier], CultureInfo.InvariantCulture));
        Assert.Equal(
            HighlightRuleOptions.DefaultMinShortWindowCount.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightMinShortWindowCount]);
        Assert.Equal(
            HighlightRuleOptions.DefaultCooldownSeconds.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightCooldownSeconds]);
        Assert.Equal(string.Empty, settings.Values[AppSettingKeys.HighlightBoostKeywords]);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task SaveHighlightAsync_clamps_out_of_range_values_through_store()
    {
        var (vm, settings, _, _) = CreateViewModel();
        vm.HighlightShortWindow = 9999m;
        vm.HighlightCooldownSeconds = -5m;

        await vm.SaveHighlightAsync();

        Assert.Equal("600", settings.Values[AppSettingKeys.HighlightShortWindowSeconds]);
        Assert.Equal("0", settings.Values[AppSettingKeys.HighlightCooldownSeconds]);
        Assert.Equal(600m, vm.HighlightShortWindow);
        Assert.Equal(0m, vm.HighlightCooldownSeconds);
    }

    [Fact]
    public async Task ResetHighlightAsync_writes_defaults_and_reloads_form()
    {
        var (vm, settings, _, _) = CreateViewModel();
        settings.Values[AppSettingKeys.HighlightShortWindowSeconds] = "60";
        settings.Values[AppSettingKeys.HighlightCooldownSeconds] = "5";
        await vm.LoadAsync();
        Assert.Equal(60m, vm.HighlightShortWindow);

        await vm.ResetHighlightAsync();

        Assert.Equal(
            HighlightRuleOptions.DefaultShortWindowSeconds.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightShortWindowSeconds]);
        Assert.Equal(
            HighlightRuleOptions.DefaultCooldownSeconds.ToString(CultureInfo.InvariantCulture),
            settings.Values[AppSettingKeys.HighlightCooldownSeconds]);
        Assert.Equal(HighlightRuleOptions.DefaultShortWindowSeconds, (int)vm.HighlightShortWindow!.Value);
        Assert.Equal(HighlightRuleOptions.DefaultCooldownSeconds, (int)vm.HighlightCooldownSeconds!.Value);
        Assert.Equal("666\n草\n神回\nPog", vm.HighlightBoostKeywords);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("高能参数已恢复默认值。", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveHighlightAsync_reports_failure()
    {
        var (vm, settings, _, _) = CreateViewModel();
        settings.SetException = new InvalidOperationException("disk full");

        await vm.SaveHighlightAsync();

        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("保存高能参数失败", vm.StatusText);
        Assert.Contains("disk full", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveHighlightAsync_sets_busy_and_ignores_reentry()
    {
        var (vm, settings, _, _) = CreateViewModel();
        settings.SetRelease = NewSignal();
        var propertyChanges = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        var task = vm.SaveHighlightAsync();
        await settings.SetEntered;

        Assert.True(vm.IsBusy);
        Assert.False(vm.IsNotBusy);
        Assert.Equal(1, settings.SetAttempts);
        Assert.Contains(nameof(GeneralAndDanmakuSettingsViewModel.IsNotBusy), propertyChanges);

        // 保存进行中再次点击不应重复写库。
        await vm.SaveHighlightAsync();
        Assert.Equal(1, settings.SetAttempts);

        settings.SetRelease!.SetResult();
        await task;

        Assert.False(vm.IsBusy);
        Assert.Equal(6, settings.SetAttempts); // 5 个数字 + 关键词
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task SaveTwitchProxyAsync_blank_template_saves_empty_and_disables_proxy()
    {
        var (vm, settings, _, applied) = CreateViewModel();
        vm.TwitchProxyTemplate = "   ";

        await vm.SaveTwitchProxyAsync();

        Assert.Equal(string.Empty, settings.Values[AppSettingKeys.TwitchPlaylistProxyUrl]);
        Assert.Equal(new string?[] { null }, applied);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("已保存：Twitch 仅使用内置原生播放列表（GQL + Usher）。", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveTwitchProxyAsync_saves_trimmed_template_and_applies_it()
    {
        var (vm, settings, _, applied) = CreateViewModel();
        vm.TwitchProxyTemplate = " https://proxy.example/live.m3u8 ";

        await vm.SaveTwitchProxyAsync();

        Assert.Equal("https://proxy.example/live.m3u8", settings.Values[AppSettingKeys.TwitchPlaylistProxyUrl]);
        Assert.Equal(new string?[] { "https://proxy.example/live.m3u8" }, applied);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
        Assert.Equal("已保存：Twitch 将优先使用自定义去广告代理。", vm.StatusText);
    }

    [Fact]
    public async Task SaveTwitchProxyAsync_does_not_validate_url_format()
    {
        var (vm, settings, _, applied) = CreateViewModel();
        vm.TwitchProxyTemplate = "not a url";

        await vm.SaveTwitchProxyAsync();

        // 与旧实现一致：不校验格式，非空模板原样交给播放层，代理不可用由播放层降级。
        Assert.Equal("not a url", settings.Values[AppSettingKeys.TwitchPlaylistProxyUrl]);
        Assert.Equal(new string?[] { "not a url" }, applied);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task SaveTwitchProxyAsync_reports_failure_without_applying()
    {
        var (vm, settings, _, applied) = CreateViewModel();
        settings.SetException = new InvalidOperationException("io error");
        vm.TwitchProxyTemplate = "https://proxy.example/live.m3u8";

        await vm.SaveTwitchProxyAsync();

        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("保存 Twitch 代理失败", vm.StatusText);
        Assert.Contains("io error", vm.StatusText);
        Assert.Empty(applied);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SetUnicycleEnabledAsync_persists_value_and_reports()
    {
        var (vm, settings, _, _) = CreateViewModel();

        await vm.SetUnicycleEnabledAsync(true);

        Assert.True(vm.UnicycleEnabled);
        Assert.Equal("true", settings.Values[AppSettingKeys.BlockUnicycleEnabled]);
        Assert.Equal("独轮车自动屏蔽已开启。", vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);

        await vm.SetUnicycleEnabledAsync(false);

        Assert.False(vm.UnicycleEnabled);
        Assert.Equal("false", settings.Values[AppSettingKeys.BlockUnicycleEnabled]);
        Assert.Equal("独轮车自动屏蔽已关闭。", vm.StatusText);
    }

    [Fact]
    public async Task SetDanmakuPreviewEnabledAsync_persists_value_and_reports()
    {
        var (vm, settings, _, _) = CreateViewModel();

        await vm.SetDanmakuPreviewEnabledAsync(true);

        Assert.True(vm.DanmakuPreviewEnabled);
        Assert.Equal("true", settings.Values[AppSettingKeys.DanmakuPreviewEnabled]);
        Assert.Equal("监控画面弹幕预览已开启。", vm.StatusText);

        await vm.SetDanmakuPreviewEnabledAsync(false);

        Assert.False(vm.DanmakuPreviewEnabled);
        Assert.Equal("false", settings.Values[AppSettingKeys.DanmakuPreviewEnabled]);
        Assert.Equal("监控画面弹幕预览已关闭。", vm.StatusText);
    }

    [Fact]
    public async Task SetDanmakuStickerVisibleAsync_persists_value_and_reports()
    {
        var (vm, settings, _, _) = CreateViewModel();

        await vm.SetDanmakuStickerVisibleAsync(false);

        Assert.False(vm.DanmakuStickerVisible);
        Assert.Equal("false", settings.Values[AppSettingKeys.DanmakuStickerVisible]);
        Assert.Equal("纯表情包仅参与统计与高能检测，不再渲染。", vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);

        await vm.SetDanmakuStickerVisibleAsync(true);

        Assert.True(vm.DanmakuStickerVisible);
        Assert.Equal("true", settings.Values[AppSettingKeys.DanmakuStickerVisible]);
        Assert.Equal("纯表情包会渲染为 [表情名] 文本。", vm.StatusText);
    }

    [Fact]
    public async Task SetDanmakuEmoteImagesVisibleAsync_persists_value_and_reports()
    {
        var (vm, settings, _, _) = CreateViewModel();

        await vm.SetDanmakuEmoteImagesVisibleAsync(false);

        Assert.False(vm.DanmakuEmoteImagesVisible);
        Assert.Equal("false", settings.Values[AppSettingKeys.DanmakuEmoteImagesVisible]);
        Assert.Equal("表情图片已关闭，表情按 [表情] 文本显示。", vm.StatusText);

        await vm.SetDanmakuEmoteImagesVisibleAsync(true);

        Assert.True(vm.DanmakuEmoteImagesVisible);
        Assert.Equal("true", settings.Values[AppSettingKeys.DanmakuEmoteImagesVisible]);
        Assert.Equal("表情图片已开启（仅渲染静态帧，未加载成功的表情先显示 [表情名]）。", vm.StatusText);
        Assert.Equal(GeneralAndDanmakuStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task Toggle_setters_report_failure_but_keep_local_state()
    {
        var (vm, settings, _, _) = CreateViewModel();
        settings.SetException = new InvalidOperationException("write blocked");

        await vm.SetDanmakuStickerVisibleAsync(false);

        Assert.False(vm.DanmakuStickerVisible);
        Assert.Equal(GeneralAndDanmakuStatusKind.Error, vm.StatusKind);
        Assert.Contains("保存表情包渲染开关失败", vm.StatusText);
        Assert.Contains("write blocked", vm.StatusText);
    }

    [Fact]
    public async Task Toggle_setters_do_not_persist_while_loading()
    {
        var (vm, settings, blocklist, _) = CreateViewModel();
        blocklist.GetAllRelease = NewSignal();
        var task = vm.LoadAsync();
        await blocklist.GetAllEntered;

        await vm.SetUnicycleEnabledAsync(true);
        await vm.SetDanmakuPreviewEnabledAsync(true);
        await vm.SetDanmakuStickerVisibleAsync(false);
        await vm.SetDanmakuEmoteImagesVisibleAsync(false);

        Assert.Equal(0, settings.SetAttempts);
        Assert.True(vm.UnicycleEnabled);
        Assert.True(vm.DanmakuPreviewEnabled);
        Assert.False(vm.DanmakuStickerVisible);
        Assert.False(vm.DanmakuEmoteImagesVisible);
        Assert.Equal(string.Empty, vm.StatusText);

        blocklist.GetAllRelease!.SetResult();
        await task;
    }

    [Fact]
    public async Task Production_constructor_applies_proxy_template_to_TwitchAdConfig()
    {
        var original = TwitchAdConfig.PlaylistProxyTemplateProvider;
        try
        {
            var vm = new GeneralAndDanmakuSettingsViewModel(
                new TestAppSettingsRepository(),
                new TestBlocklistRepository());
            vm.TwitchProxyTemplate = "https://proxy.example/live.m3u8";

            await vm.SaveTwitchProxyAsync();

            var provider = TwitchAdConfig.PlaylistProxyTemplateProvider;
            Assert.NotNull(provider);
            Assert.Equal("https://proxy.example/live.m3u8", provider!());

            vm.TwitchProxyTemplate = string.Empty;
            await vm.SaveTwitchProxyAsync();

            provider = TwitchAdConfig.PlaylistProxyTemplateProvider;
            Assert.NotNull(provider);
            Assert.Null(provider!());
        }
        finally
        {
            TwitchAdConfig.PlaylistProxyTemplateProvider = original;
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (
        GeneralAndDanmakuSettingsViewModel Vm,
        TestAppSettingsRepository Settings,
        TestBlocklistRepository Blocklist,
        List<string?> Applied) CreateViewModel()
    {
        var settings = new TestAppSettingsRepository();
        var blocklist = new TestBlocklistRepository();
        var applied = new List<string?>();
        var vm = new GeneralAndDanmakuSettingsViewModel(settings, blocklist, applied.Add);
        return (vm, settings, blocklist, applied);
    }

    private sealed class TestAppSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _setEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>当前键值内容；测试可直接读写以预置/断言配置。</summary>
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>设置后 <see cref="GetAllAsync"/> 抛出该异常（用于读取失败分支）。</summary>
        public Exception? GetAllException { get; set; }

        /// <summary>设置后 <see cref="SetAsync"/> 抛出该异常（用于保存失败分支）。</summary>
        public Exception? SetException { get; set; }

        /// <summary>设置后 SetAsync 在写入前挂起，直到测试调用 SetResult（用于忙状态断言）。</summary>
        public TaskCompletionSource? SetRelease { get; set; }

        public int SetAttempts { get; private set; }

        public Task SetEntered => _setEntered.Task;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(Values.TryGetValue(key, out var value) ? value : null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        {
            if (GetAllException is not null)
            {
                throw GetAllException;
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(Values));
        }

        public async Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            SetAttempts++;
            if (SetException is not null)
            {
                throw SetException;
            }

            if (SetRelease is not null)
            {
                _setEntered.TrySetResult();
                await SetRelease.Task.ConfigureAwait(false);
            }

            Values[key] = value;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class TestBlocklistRepository : IBlocklistRepository
    {
        private readonly List<DanmakuBlockKeyword> _keywords = new();
        private readonly TaskCompletionSource _getAllEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DanmakuBlockKeyword> Keywords => _keywords;

        /// <summary>设置后 <see cref="GetAllAsync"/> 抛出该异常（用于加载失败分支）。</summary>
        public Exception? GetAllException { get; set; }

        /// <summary>设置后 <see cref="AddAsync"/> 抛出该异常（用于新增失败分支）。</summary>
        public Exception? AddException { get; set; }

        /// <summary>设置后 <see cref="DeleteAsync"/> 抛出该异常（用于删除失败分支）。</summary>
        public Exception? DeleteException { get; set; }

        /// <summary>替换重复判定结果（默认按关键词精确匹配内存列表）。</summary>
        public Func<string, bool>? ExistsOverride { get; set; }

        /// <summary>设置后 GetAllAsync 在返回前挂起，直到测试调用 SetResult（用于加载状态断言）。</summary>
        public TaskCompletionSource? GetAllRelease { get; set; }

        public int AddAttempts { get; private set; }

        public int DeleteAttempts { get; private set; }

        public Guid? LastDeletedId { get; private set; }

        public Task GetAllEntered => _getAllEntered.Task;

        public DanmakuBlockKeyword Seed(string keyword, bool isRegex)
        {
            var entity = new DanmakuBlockKeyword
            {
                Id = Guid.NewGuid(),
                Keyword = keyword,
                IsRegex = isRegex,
                CreatedAt = DateTime.UtcNow
            };
            _keywords.Add(entity);
            return entity;
        }

        public async Task<IReadOnlyList<DanmakuBlockKeyword>> GetAllAsync(CancellationToken ct = default)
        {
            if (GetAllException is not null)
            {
                throw GetAllException;
            }

            if (GetAllRelease is not null)
            {
                _getAllEntered.TrySetResult();
                await GetAllRelease.Task.ConfigureAwait(false);
            }

            return _keywords.ToList();
        }

        public Task AddAsync(DanmakuBlockKeyword keyword, CancellationToken ct = default)
        {
            AddAttempts++;
            if (AddException is not null)
            {
                throw AddException;
            }

            _keywords.Add(keyword);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            DeleteAttempts++;
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            LastDeletedId = id;
            _keywords.RemoveAll(keyword => keyword.Id == id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string keyword, CancellationToken ct = default)
            => Task.FromResult(
                ExistsOverride?.Invoke(keyword)
                ?? _keywords.Any(item => string.Equals(item.Keyword, keyword, StringComparison.Ordinal)));
    }
}
