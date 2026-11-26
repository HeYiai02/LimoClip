using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>ASR 分区状态文本的语气（决定前景色，由 View 映射为画刷）。</summary>
public enum AsrSettingsStatusKind
{
    Neutral,
    Success,
    Error
}

/// <summary>
/// 设置页「语音识别（ASR）」子块：预设/接口/模型/语言表单状态、读取与保存、
/// 自定义预设校验、连通性测试以及加载/保存忙状态。窗口只保留事件转发与状态配色。
/// 依赖 <see cref="AsrSettingsStore"/>（内部再依赖设置仓储与 HTTP 客户端），
/// 本类不引用任何 Avalonia UI 类型，便于在无 UI 的单元测试中驱动。
/// </summary>
public sealed class AsrSettingsViewModel : ObservableObject
{
    /// <summary>ASR 语言下拉项与持久化 Id 一一对应（下标相同）。</summary>
    private static readonly string[] LanguageIds = { "auto", "zh", "en", "ja", "ko" };

    /// <summary>语言下拉显示名，与 <see cref="LanguageIds"/> 同下标。</summary>
    public IReadOnlyList<string> LanguageNames { get; } = AsrSettingsStatusText.LanguageNames;

    private readonly AsrSettingsStore _store;

    private AsrPreset? _selectedPreset;
    private string _endpoint = string.Empty;
    private string _model = string.Empty;
    private int _languageIndex;
    private string _apiKey = string.Empty;
    private bool _hasApiKey;
    private string _presetDescription = string.Empty;
    private string _statusText = AsrSettingsStatusText.Initial;
    private AsrSettingsStatusKind _statusKind = AsrSettingsStatusKind.Neutral;
    private bool _isBusy;
    private bool _isLoading;
    private string _testButtonText = AsrSettingsStatusText.TestButtonIdle;

    public AsrSettingsViewModel(AsrSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>预设下拉数据源（设置页「识别服务」）。</summary>
    public IReadOnlyList<AsrPreset> Presets { get; } = AsrSettingsStore.Presets;

    /// <summary>当前选中的预设；用户切换时把接口地址/模型填成预设默认值（加载期间除外）。</summary>
    public AsrPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value is null || IsLoading)
            {
                return;
            }

            Endpoint = value.Endpoint;
            Model = value.Model;
            PresetDescription = value.Description;
            if (IsCustomPreset(value))
            {
                SetStatus(AsrSettingsStatusText.CustomPresetHint, AsrSettingsStatusKind.Neutral);
            }
            else
            {
                SetStatus(AsrSettingsStatusText.PresetSwitched(value.DisplayName), AsrSettingsStatusKind.Neutral);
            }
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value ?? string.Empty);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value ?? string.Empty);
    }

    /// <summary>语言下拉的选中下标；越界时保存为默认语言。</summary>
    public int LanguageIndex
    {
        get => _languageIndex;
        set => SetProperty(ref _languageIndex, value);
    }

    /// <summary>API Key 明文输入框内容；保存成功后清空，读取后也清空，绝不回填已保存的 Key。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value ?? string.Empty);
    }

    /// <summary>是否已有加密保存的 API Key（只影响输入框 Watermark，不回传明文）。</summary>
    public bool HasApiKey
    {
        get => _hasApiKey;
        private set
        {
            if (SetProperty(ref _hasApiKey, value))
            {
                OnPropertyChanged(nameof(ApiKeyWatermark));
            }
        }
    }

    public string ApiKeyWatermark => HasApiKey ? AsrSettingsStatusText.WatermarkSaved : AsrSettingsStatusText.WatermarkEmpty;

    public string PresetDescription
    {
        get => _presetDescription;
        private set => SetProperty(ref _presetDescription, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public AsrSettingsStatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    /// <summary>保存/测试进行中（按钮禁用由 View 绑定 <see cref="IsNotBusy"/> 实现）。</summary>
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

    /// <summary>正在把快照回填到表单（抑制预设切换的默认值副作用）。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>测试连接按钮文案；测试进行中显示「正在测试…」。</summary>
    public string TestButtonText
    {
        get => _testButtonText;
        private set => SetProperty(ref _testButtonText, value);
    }

    /// <summary>读取 ASR 快照并回填表单；Key 只反映是否存在，绝不回显明文。异常收敛为错误状态，不向外抛。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var snapshot = await _store.LoadAsync(ct).ConfigureAwait(true);
            SelectedPreset = AsrSettingsStore.ResolvePreset(snapshot.PresetId);
            Endpoint = snapshot.Endpoint;
            Model = snapshot.Model;
            int languageIndex = Array.IndexOf(LanguageIds, snapshot.Language);
            LanguageIndex = languageIndex >= 0 ? languageIndex : 0;
            PresetDescription = SelectedPreset?.Description ?? string.Empty;
            // 重读时清空明文输入，避免上一次未保存的 Key 停留在表单里。
            ApiKey = string.Empty;
            HasApiKey = snapshot.HasApiKey;
            SetStatus(
                snapshot.HasApiKey ? AsrSettingsStatusText.LoadedConfigured : AsrSettingsStatusText.LoadedMissingApiKey,
                AsrSettingsStatusKind.Neutral);
        }
        catch (Exception ex)
        {
            SetStatus(AsrSettingsStatusText.LoadFailed(ex.Message), AsrSettingsStatusKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>保存表单：自定义预设先校验，成功后清空明文 Key 并重新读取以刷新 Watermark。</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var preset = SelectedPreset ?? AsrSettingsStore.ResolvePreset(null);
        var draft = BuildDraft(preset);
        if (RejectInvalidCustomPreset(preset, draft))
        {
            return;
        }

        SetBusy(true, AsrSettingsStatusText.Saving);
        try
        {
            await _store.SaveAsync(draft, ct).ConfigureAwait(true);
            // 保存成功后清空明文 Key 并重新读取，刷新 Watermark 与表单内容。
            ApiKey = string.Empty;
            await LoadAsync(ct).ConfigureAwait(true);
            SetStatus(AsrSettingsStatusText.Saved, AsrSettingsStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus(AsrSettingsStatusText.SaveFailed(ex.Message), AsrSettingsStatusKind.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>仅用当前表单草稿测试连通性，不写入 AppSettings。</summary>
    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var preset = SelectedPreset ?? AsrSettingsStore.ResolvePreset(null);
        var draft = BuildDraft(preset);
        if (RejectInvalidCustomPreset(preset, draft))
        {
            return;
        }

        SetBusy(true, AsrSettingsStatusText.Testing);
        TestButtonText = AsrSettingsStatusText.TestButtonRunning;
        try
        {
            var result = await _store.TestConnectionAsync(draft, ct).ConfigureAwait(true);
            SetStatus(
                result.Message,
                result.Success ? AsrSettingsStatusKind.Success : AsrSettingsStatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus(AsrSettingsStatusText.TestFailed(ex.Message), AsrSettingsStatusKind.Error);
        }
        finally
        {
            TestButtonText = AsrSettingsStatusText.TestButtonIdle;
            SetBusy(false, null);
        }
    }

    /// <summary>把表单内容拼成保存/测试共用草稿；Key 留空表示保留原值。</summary>
    private AsrSettingsDraft BuildDraft(AsrPreset preset)
    {
        string? plainApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
        return new AsrSettingsDraft(
            preset.Id,
            Endpoint,
            Model,
            GetSelectedLanguageId(),
            plainApiKey);
    }

    /// <summary>自定义服务必须填写接口地址与模型；校验失败时返回 true 并已写状态文本。</summary>
    private bool RejectInvalidCustomPreset(AsrPreset preset, AsrSettingsDraft draft)
    {
        if (!IsCustomPreset(preset))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(draft.Endpoint) && !string.IsNullOrWhiteSpace(draft.Model))
        {
            return false;
        }

        SetStatus(AsrSettingsStatusText.InvalidCustomPreset, AsrSettingsStatusKind.Error);
        return true;
    }

    private static bool IsCustomPreset(AsrPreset preset)
        => string.Equals(preset.Id, "custom", StringComparison.OrdinalIgnoreCase);

    private string GetSelectedLanguageId()
    {
        int index = LanguageIndex;
        return index >= 0 && index < LanguageIds.Length
            ? LanguageIds[index]
            : AsrSettingsStore.DefaultLanguage;
    }

    /// <summary>更新分区状态文本并按语气记录类型（画刷由 View 映射）。</summary>
    private void SetStatus(string text, AsrSettingsStatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    /// <summary>进入/退出保存或测试：切换忙状态，可附带一条状态提示。</summary>
    private void SetBusy(bool busy, string? statusText)
    {
        IsBusy = busy;
        if (statusText is not null)
        {
            SetStatus(statusText, AsrSettingsStatusKind.Neutral);
        }
    }
}
