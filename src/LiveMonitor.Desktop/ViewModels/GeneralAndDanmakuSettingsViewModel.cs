using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using LiveMonitor.Platforms;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>设置页「通用与弹幕」子块状态文本的语气（决定前景色，由 View 映射为画刷）。</summary>
public enum GeneralAndDanmakuStatusKind
{
    Neutral,
    Success,
    Error
}

/// <summary>
/// 设置页「通用与弹幕」子块：屏蔽词库、独轮车/弹幕渲染开关、高能检测参数与 Twitch 去广告代理的
/// 表单状态、读取/保存/校验/重置与状态文案。窗口只保留事件转发、状态配色与控件挂载。
/// 依赖 <see cref="IAppSettingsRepository"/>、<see cref="IBlocklistRepository"/> 与高能参数存储
/// <see cref="HighlightSettingsStore"/>；Twitch 代理的应用动作以委托注入（生产环境写入
/// <see cref="TwitchAdConfig.PlaylistProxyTemplateProvider"/>，测试可替换为捕获委托），
/// 本类不引用任何 Avalonia UI 类型，便于在无 UI/无网络的单元测试中驱动。
/// </summary>
public sealed class GeneralAndDanmakuSettingsViewModel : ObservableObject
{
    private readonly IAppSettingsRepository _settings;
    private readonly IBlocklistRepository _blocklist;

    /// <summary>把规范化后的代理模板写入运行时配置；参数为 null 表示不使用外部代理。</summary>
    private readonly Action<string?> _applyTwitchProxyTemplate;

    private string _newKeywordText = string.Empty;
    private bool _newKeywordIsRegex;
    private DanmakuBlockKeyword? _selectedKeyword;
    private bool _unicycleEnabled;
    private bool _danmakuPreviewEnabled;
    private bool _danmakuStickerVisible;
    private bool _danmakuEmoteImagesVisible;
    private decimal? _highlightShortWindow;
    private decimal? _highlightBaselineWindow;
    private decimal? _highlightDensityMultiplier;
    private decimal? _highlightMinShortWindowCount;
    private decimal? _highlightCooldownSeconds;
    private string _highlightBoostKeywords = string.Empty;
    private string _twitchProxyTemplate = string.Empty;
    private string _statusText = string.Empty;
    private GeneralAndDanmakuStatusKind _statusKind = GeneralAndDanmakuStatusKind.Neutral;
    private bool _isBusy;
    private bool _isLoading;

    /// <summary>生产构造：直接复用组合根中的设置仓储与屏蔽词仓储。</summary>
    public GeneralAndDanmakuSettingsViewModel(
        IAppSettingsRepository settings,
        IBlocklistRepository blocklist)
        : this(settings, blocklist, ApplyTwitchProxyTemplate)
    {
    }

    /// <summary>测试构造：把 Twitch 代理模板的应用动作替换成捕获委托，避免改动进程级静态配置。</summary>
    public GeneralAndDanmakuSettingsViewModel(
        IAppSettingsRepository settings,
        IBlocklistRepository blocklist,
        Action<string?> applyTwitchProxyTemplate)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _blocklist = blocklist ?? throw new ArgumentNullException(nameof(blocklist));
        _applyTwitchProxyTemplate = applyTwitchProxyTemplate
            ?? throw new ArgumentNullException(nameof(applyTwitchProxyTemplate));
    }

    /// <summary>屏蔽词列表（视图直接绑定 ItemsSource，增删后整表刷新）。</summary>
    public ObservableCollection<DanmakuBlockKeyword> Keywords { get; } = new();

    /// <summary>新增屏蔽词的输入框内容（保存成功后清空）。</summary>
    public string NewKeywordText
    {
        get => _newKeywordText;
        set => SetProperty(ref _newKeywordText, value ?? string.Empty);
    }

    /// <summary>新增屏蔽词是否按正则匹配（保存成功后重置为 false）。</summary>
    public bool NewKeywordIsRegex
    {
        get => _newKeywordIsRegex;
        set => SetProperty(ref _newKeywordIsRegex, value);
    }

    /// <summary>屏蔽词列表当前选中项（「删除选中」的数据来源，列表刷新后自动清空）。</summary>
    public DanmakuBlockKeyword? SelectedKeyword
    {
        get => _selectedKeyword;
        set => SetProperty(ref _selectedKeyword, value);
    }

    /// <summary>独轮车自动屏蔽开关；键值只有显式 "true" 才视为开启。</summary>
    public bool UnicycleEnabled
    {
        get => _unicycleEnabled;
        set => SetProperty(ref _unicycleEnabled, value);
    }

    /// <summary>监控画面弹幕预览开关；键值只有显式 "true" 才视为开启。</summary>
    public bool DanmakuPreviewEnabled
    {
        get => _danmakuPreviewEnabled;
        set => SetProperty(ref _danmakuPreviewEnabled, value);
    }

    /// <summary>纯表情包渲染开关；键缺失时默认开启，只有显式 "false" 才关闭。</summary>
    public bool DanmakuStickerVisible
    {
        get => _danmakuStickerVisible;
        set => SetProperty(ref _danmakuStickerVisible, value);
    }

    /// <summary>表情图片渲染开关；键缺失时默认开启，只有显式 "false" 才关闭。</summary>
    public bool DanmakuEmoteImagesVisible
    {
        get => _danmakuEmoteImagesVisible;
        set => SetProperty(ref _danmakuEmoteImagesVisible, value);
    }

    /// <summary>高能检测短窗秒数表单值；null 表示沿用默认值。</summary>
    public decimal? HighlightShortWindow
    {
        get => _highlightShortWindow;
        set => SetProperty(ref _highlightShortWindow, value);
    }

    /// <summary>高能检测基线窗秒数表单值；null 表示沿用默认值。</summary>
    public decimal? HighlightBaselineWindow
    {
        get => _highlightBaselineWindow;
        set => SetProperty(ref _highlightBaselineWindow, value);
    }

    /// <summary>高能检测密度倍数表单值；null 表示沿用默认值。</summary>
    public decimal? HighlightDensityMultiplier
    {
        get => _highlightDensityMultiplier;
        set => SetProperty(ref _highlightDensityMultiplier, value);
    }

    /// <summary>高能检测短窗最少条数表单值；null 表示沿用默认值。</summary>
    public decimal? HighlightMinShortWindowCount
    {
        get => _highlightMinShortWindowCount;
        set => SetProperty(ref _highlightMinShortWindowCount, value);
    }

    /// <summary>高能检测冷却秒数表单值；null 表示沿用默认值。</summary>
    public decimal? HighlightCooldownSeconds
    {
        get => _highlightCooldownSeconds;
        set => SetProperty(ref _highlightCooldownSeconds, value);
    }

    /// <summary>加权关键词文本（一行一个，保存前由 <see cref="HighlightSettingsStore"/> 解析并去重）。</summary>
    public string HighlightBoostKeywords
    {
        get => _highlightBoostKeywords;
        set => SetProperty(ref _highlightBoostKeywords, value ?? string.Empty);
    }

    /// <summary>Twitch 去广告代理模板输入框内容；留空表示仅使用内置原生播放列表。</summary>
    public string TwitchProxyTemplate
    {
        get => _twitchProxyTemplate;
        set => SetProperty(ref _twitchProxyTemplate, value ?? string.Empty);
    }

    /// <summary>本子块状态文案（底部共享状态栏由 View 转发显示）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>状态文案语气（前景色由 View 映射为画刷）。</summary>
    public GeneralAndDanmakuStatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    /// <summary>屏蔽词/高能参数/代理保存进行中（阻止重入，View 用 <see cref="IsNotBusy"/> 禁用按钮）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>正在把快照回填到表单：期间用户改动（含绑定触发的开关事件）不得写库。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// 读取屏蔽词、四个弹幕开关、高能参数与 Twitch 代理并回填表单；异常收敛为错误状态，不向外抛。
    /// 开关键的缺省语义与旧实现一致：独轮车/预览只有显式 "true" 才开启；
    /// 表情包/表情图片缺失时默认开启，只有显式 "false" 才关闭。
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            await ReloadKeywordsAsync(ct).ConfigureAwait(true);

            var unicycle = await _settings.GetAsync(AppSettingKeys.BlockUnicycleEnabled, ct).ConfigureAwait(true);
            UnicycleEnabled = string.Equals(unicycle, "true", StringComparison.OrdinalIgnoreCase);

            var preview = await _settings.GetAsync(AppSettingKeys.DanmakuPreviewEnabled, ct).ConfigureAwait(true);
            DanmakuPreviewEnabled = string.Equals(preview, "true", StringComparison.OrdinalIgnoreCase);

            var sticker = await _settings.GetAsync(AppSettingKeys.DanmakuStickerVisible, ct).ConfigureAwait(true);
            DanmakuStickerVisible = IsEnabledUnlessExplicitlyFalse(sticker);

            var emoteImages = await _settings.GetAsync(AppSettingKeys.DanmakuEmoteImagesVisible, ct).ConfigureAwait(true);
            DanmakuEmoteImagesVisible = IsEnabledUnlessExplicitlyFalse(emoteImages);

            await LoadHighlightSettingsAsync(ct).ConfigureAwait(true);
            await LoadTwitchProxyAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus($"读取设置失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 新增屏蔽词：空输入静默忽略（与旧实现一致）、重复词提示不落库；
    /// 成功后清空输入框与正则勾选并整表刷新。
    /// </summary>
    public async Task AddKeywordAsync(CancellationToken ct = default)
    {
        var keyword = NewKeywordText.Trim();
        if (keyword.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _blocklist.ExistsAsync(keyword, ct).ConfigureAwait(true))
            {
                SetStatus("这个词已经在屏蔽词库中。", GeneralAndDanmakuStatusKind.Neutral);
                return;
            }

            bool isRegex = NewKeywordIsRegex;
            await _blocklist.AddAsync(
                new DanmakuBlockKeyword
                {
                    Id = Guid.NewGuid(),
                    Keyword = keyword,
                    IsRegex = isRegex,
                    CreatedAt = DateTime.UtcNow
                },
                ct).ConfigureAwait(true);
            NewKeywordText = string.Empty;
            NewKeywordIsRegex = false;
            await ReloadKeywordsAsync(ct).ConfigureAwait(true);
            SetStatus(
                isRegex ? "已添加正则屏蔽词。" : "已添加屏蔽词。",
                GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"添加失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除当前选中的屏蔽词；未选中时只提示、不落库（与旧实现一致）。</summary>
    public async Task RemoveKeywordAsync(CancellationToken ct = default)
    {
        var keyword = SelectedKeyword;
        if (keyword is null)
        {
            SetStatus("请先选中一个屏蔽词。", GeneralAndDanmakuStatusKind.Neutral);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _blocklist.DeleteAsync(keyword.Id, ct).ConfigureAwait(true);
            await ReloadKeywordsAsync(ct).ConfigureAwait(true);
            SetStatus("已删除屏蔽词。", GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存高能检测参数（规范化后写 6 个键），成功后重新读取回填；异常收敛为错误状态。</summary>
    public async Task SaveHighlightAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await HighlightSettingsStore.SaveAsync(_settings, ReadHighlightOptions(), ct).ConfigureAwait(true);
            await LoadHighlightSettingsAsync(ct).ConfigureAwait(true);
            SetStatus("高能参数已保存（对运行中的会话即时生效）。", GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"保存高能参数失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把高能检测参数恢复默认值并重新读取回填；异常收敛为错误状态。</summary>
    public async Task ResetHighlightAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await HighlightSettingsStore.SaveAsync(_settings, HighlightRuleOptions.Default, ct).ConfigureAwait(true);
            await LoadHighlightSettingsAsync(ct).ConfigureAwait(true);
            SetStatus("高能参数已恢复默认值。", GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"恢复默认值失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 保存 Twitch 去广告代理模板（去空白后写 AppSettings）并应用到运行时配置；
    /// 留空表示仅使用内置原生播放列表。这里刻意不对 URL 做格式校验（与旧实现一致）：
    /// 任何非空模板都原样保存并交给播放层，代理不可用由播放层自动降级。
    /// </summary>
    public async Task SaveTwitchProxyAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var template = NormalizeTwitchProxyTemplate(TwitchProxyTemplate);
        try
        {
            await _settings.SetAsync(
                AppSettingKeys.TwitchPlaylistProxyUrl,
                template ?? string.Empty,
                ct).ConfigureAwait(true);
            _applyTwitchProxyTemplate(template);
            SetStatus(
                template is null
                    ? "已保存：Twitch 仅使用内置原生播放列表（GQL + Usher）。"
                    : "已保存：Twitch 将优先使用自定义去广告代理。",
                GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"保存 Twitch 代理失败：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>独轮车自动屏蔽开关：加载期间的事件不落库（由 <see cref="IsLoading"/> 守卫）。</summary>
    public async Task SetUnicycleEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        UnicycleEnabled = enabled;
        if (IsLoading)
        {
            return;
        }

        await PersistToggleAsync(
            AppSettingKeys.BlockUnicycleEnabled,
            enabled,
            enabled ? "独轮车自动屏蔽已开启。" : "独轮车自动屏蔽已关闭。",
            "保存开关失败",
            ct).ConfigureAwait(true);
    }

    /// <summary>监控画面弹幕预览开关；加载期间的事件不落库。</summary>
    public async Task SetDanmakuPreviewEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        DanmakuPreviewEnabled = enabled;
        if (IsLoading)
        {
            return;
        }

        await PersistToggleAsync(
            AppSettingKeys.DanmakuPreviewEnabled,
            enabled,
            enabled ? "监控画面弹幕预览已开启。" : "监控画面弹幕预览已关闭。",
            "保存弹幕预览开关失败",
            ct).ConfigureAwait(true);
    }

    /// <summary>纯表情包渲染开关；加载期间的事件不落库。</summary>
    public async Task SetDanmakuStickerVisibleAsync(bool visible, CancellationToken ct = default)
    {
        DanmakuStickerVisible = visible;
        if (IsLoading)
        {
            return;
        }

        await PersistToggleAsync(
            AppSettingKeys.DanmakuStickerVisible,
            visible,
            visible ? "纯表情包会渲染为 [表情名] 文本。" : "纯表情包仅参与统计与高能检测，不再渲染。",
            "保存表情包渲染开关失败",
            ct).ConfigureAwait(true);
    }

    /// <summary>表情图片渲染开关；加载期间的事件不落库。</summary>
    public async Task SetDanmakuEmoteImagesVisibleAsync(bool visible, CancellationToken ct = default)
    {
        DanmakuEmoteImagesVisible = visible;
        if (IsLoading)
        {
            return;
        }

        await PersistToggleAsync(
            AppSettingKeys.DanmakuEmoteImagesVisible,
            visible,
            visible
                ? "表情图片已开启（仅渲染静态帧，未加载成功的表情先显示 [表情名]）。"
                : "表情图片已关闭，表情按 [表情] 文本显示。",
            "保存表情图片开关失败",
            ct).ConfigureAwait(true);
    }

    /// <summary>重新读取屏蔽词并整表替换（新增/删除后刷新列表）。</summary>
    private async Task ReloadKeywordsAsync(CancellationToken ct)
    {
        var keywords = await _blocklist.GetAllAsync(ct).ConfigureAwait(true);
        Keywords.Clear();
        foreach (var keyword in keywords)
        {
            Keywords.Add(keyword);
        }
    }

    /// <summary>读取高能参数并回填表单（读取端已做缺失/非法值回落与规范化）。</summary>
    private async Task LoadHighlightSettingsAsync(CancellationToken ct)
    {
        var options = await HighlightSettingsStore.LoadAsync(_settings, ct).ConfigureAwait(true);
        HighlightShortWindow = options.ShortWindowSeconds;
        HighlightBaselineWindow = options.BaselineWindowSeconds;
        HighlightDensityMultiplier = (decimal)options.DensityMultiplier;
        HighlightMinShortWindowCount = options.MinShortWindowCount;
        HighlightCooldownSeconds = options.CooldownSeconds;
        HighlightBoostKeywords = HighlightSettingsStore.FormatBoostKeywords(options.BoostKeywords);
    }

    /// <summary>把表单值拼成高能参数草稿；数字框为空时沿用默认值，关键词按行解析。</summary>
    private HighlightRuleOptions ReadHighlightOptions()
    {
        var defaults = HighlightRuleOptions.Default;
        return new HighlightRuleOptions
        {
            ShortWindowSeconds = (int)(HighlightShortWindow ?? defaults.ShortWindowSeconds),
            BaselineWindowSeconds = (int)(HighlightBaselineWindow ?? defaults.BaselineWindowSeconds),
            DensityMultiplier = (double)(HighlightDensityMultiplier ?? (decimal)defaults.DensityMultiplier),
            MinShortWindowCount = (int)(HighlightMinShortWindowCount ?? defaults.MinShortWindowCount),
            CooldownSeconds = (int)(HighlightCooldownSeconds ?? defaults.CooldownSeconds),
            BoostKeywords = HighlightSettingsStore.ParseBoostKeywords(HighlightBoostKeywords)
        };
    }

    /// <summary>读取 Twitch 代理模板并应用（存空/缺失表示不使用外部代理）。</summary>
    private async Task LoadTwitchProxyAsync(CancellationToken ct)
    {
        var template = await _settings.GetAsync(AppSettingKeys.TwitchPlaylistProxyUrl, ct).ConfigureAwait(true);
        TwitchProxyTemplate = template ?? string.Empty;
        _applyTwitchProxyTemplate(NormalizeTwitchProxyTemplate(template));
    }

    /// <summary>开关落库的共用流程：写 AppSettings，成功/失败更新状态文案与语气。</summary>
    private async Task PersistToggleAsync(
        string key,
        bool enabled,
        string successText,
        string failurePrefix,
        CancellationToken ct)
    {
        try
        {
            await _settings.SetAsync(key, enabled ? "true" : "false", ct).ConfigureAwait(true);
            SetStatus(successText, GeneralAndDanmakuStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"{failurePrefix}：{ex.Message}", GeneralAndDanmakuStatusKind.Error);
        }
    }

    /// <summary>弹幕渲染开关的键缺省语义：缺失/空白视为开启，只有显式 false 才关闭。</summary>
    private static bool IsEnabledUnlessExplicitlyFalse(string? stored)
        => string.IsNullOrWhiteSpace(stored)
            || !string.Equals(stored, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>代理模板规范化：空白视为不使用代理，非空值去首尾空白后原样保留。</summary>
    private static string? NormalizeTwitchProxyTemplate(string? template)
        => string.IsNullOrWhiteSpace(template) ? null : template.Trim();

    /// <summary>生产环境应用代理模板：写入 <see cref="TwitchAdConfig"/> 的运行时委托。</summary>
    private static void ApplyTwitchProxyTemplate(string? normalizedTemplate)
        => TwitchAdConfig.PlaylistProxyTemplateProvider = () => normalizedTemplate;

    /// <summary>更新子块状态文本并按语气记录类型（画刷由 View 映射）。</summary>
    private void SetStatus(string text, GeneralAndDanmakuStatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }
}
