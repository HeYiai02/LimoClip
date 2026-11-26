using System.Net.Http.Headers;
using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Data;
using LiveMonitor.Media.Asr;

namespace LiveMonitor.Desktop.Services;

/// <summary>ASR 预设（设置页下拉与默认值来源）。</summary>
public sealed record AsrPreset(string Id, string DisplayName, string Endpoint, string Model, string Description);

/// <summary>设置页读取的 ASR 配置快照（API Key 只暴露是否存在，绝不回传明文）。</summary>
public sealed record AsrSettingsSnapshot(
    string PresetId,
    string Endpoint,
    string Model,
    string Language,
    bool HasApiKey);

/// <summary>设置页保存请求；<see cref="PlainApiKey"/> 为 null 表示保留原 Key，空串表示清除。</summary>
public sealed record AsrSettingsDraft(
    string PresetId,
    string Endpoint,
    string Model,
    string Language,
    string? PlainApiKey);

/// <summary>ASR 连通性测试结果。</summary>
public sealed record AsrConnectionTestResult(bool Success, string Message);

/// <summary>
/// V0.5-A 语音识别设置：预设表、API Key 的 DPAPI 加密读写、连通性测试。
/// Media 层不引用 Data，因此加密/解密集中在这里，再以 <see cref="GetConfigAsync"/> 注入 Provider。
/// </summary>
public sealed class AsrSettingsStore
{
    public const string DefaultPresetId = AsrPresetCatalog.DefaultPresetId;
    public const string DefaultLanguage = "auto";

    /// <summary>全部预设；元数据唯一来源在 <see cref="AsrPresetCatalog"/>。</summary>
    public static IReadOnlyList<AsrPreset> Presets => AsrPresetCatalog.Presets;

    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IAppSettingsRepository _settings;
    private readonly HttpClient _httpClient;

    public AsrSettingsStore(IAppSettingsRepository settings, HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public static string DefaultLanguageValue => DefaultLanguage;

    public static AsrPreset ResolvePreset(string? presetId) => AsrPresetCatalog.Resolve(presetId);

    /// <summary>读取设置页快照；Endpoint/Model 为空时回退到预设默认值。</summary>
    public async Task<AsrSettingsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var all = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        var preset = ResolvePreset(GetValue(all, AppSettingKeys.AsrProviderPreset));
        string endpoint = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrEndpoint), preset.Endpoint);
        string model = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrModel), preset.Model);
        string language = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrLanguage), DefaultLanguage);
        bool hasApiKey = DpapiCookieProtector.TryUnprotect(
            GetValue(all, AppSettingKeys.AsrEncryptedApiKey) ?? string.Empty,
            out var apiKey) && !string.IsNullOrWhiteSpace(apiKey);
        return new AsrSettingsSnapshot(preset.Id, endpoint, model, language, hasApiKey);
    }

    /// <summary>供 <see cref="OpenAiCompatibleAsrProvider"/> 读取的明文配置；缺失的 Key 返回空串，由 Provider 给出友好提示。</summary>
    public async Task<AsrApiConfig?> GetConfigAsync(CancellationToken ct = default)
    {
        var all = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        var preset = ResolvePreset(GetValue(all, AppSettingKeys.AsrProviderPreset));
        string endpoint = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrEndpoint), preset.Endpoint);
        string model = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrModel), preset.Model);
        string language = NonEmptyOrFallback(GetValue(all, AppSettingKeys.AsrLanguage), DefaultLanguage);
        string apiKey = DpapiCookieProtector.TryUnprotect(
            GetValue(all, AppSettingKeys.AsrEncryptedApiKey) ?? string.Empty,
            out var plain) ? plain : string.Empty;
        return new AsrApiConfig(preset.Id, endpoint, model, apiKey, language);
    }

    /// <summary>保存设置页内容。API Key 为 null 时保留原值；空串时清除。</summary>
    public async Task SaveAsync(AsrSettingsDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preset = ResolvePreset(draft.PresetId);
        await _settings.SetAsync(AppSettingKeys.AsrProviderPreset, preset.Id, ct).ConfigureAwait(false);
        await SetOrDeleteAsync(AppSettingKeys.AsrEndpoint, draft.Endpoint, ct).ConfigureAwait(false);
        await SetOrDeleteAsync(AppSettingKeys.AsrModel, draft.Model, ct).ConfigureAwait(false);
        await SetOrDeleteAsync(
            AppSettingKeys.AsrLanguage,
            string.IsNullOrWhiteSpace(draft.Language) ? DefaultLanguage : draft.Language,
            ct).ConfigureAwait(false);

        if (draft.PlainApiKey is not null)
        {
            string trimmed = draft.PlainApiKey.Trim();
            if (trimmed.Length == 0)
            {
                await _settings.DeleteAsync(AppSettingKeys.AsrEncryptedApiKey, ct).ConfigureAwait(false);
            }
            else
            {
                await _settings.SetAsync(
                    AppSettingKeys.AsrEncryptedApiKey,
                    DpapiCookieProtector.Protect(trimmed),
                    ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>用当前草稿（Key 为空时回退到已保存 Key）请求 {endpoint 的 /models} 验证连通性。</summary>
    public async Task<AsrConnectionTestResult> TestConnectionAsync(
        AsrSettingsDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preset = ResolvePreset(draft.PresetId);
        string endpoint = NonEmptyOrFallback(draft.Endpoint, preset.Endpoint).Trim();
        string model = NonEmptyOrFallback(draft.Model, preset.Model).Trim();
        if (endpoint.Length == 0)
        {
            return new AsrConnectionTestResult(false, AsrConnectionStatusText.MissingEndpoint);
        }

        string apiKey = draft.PlainApiKey is { } plain && !string.IsNullOrWhiteSpace(plain)
            ? plain.Trim()
            : await LoadSavedApiKeyAsync(ct).ConfigureAwait(false);
        if (apiKey.Length == 0)
        {
            return new AsrConnectionTestResult(false, AsrConnectionStatusText.MissingApiKey);
        }

        string modelsUrl;
        try
        {
            modelsUrl = BuildModelsUrl(endpoint);
        }
        catch (UriFormatException)
        {
            return new AsrConnectionTestResult(false, AsrConnectionStatusText.InvalidEndpoint(endpoint));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new AsrConnectionTestResult(
                    true,
                    AsrConnectionStatusText.Succeeded((int)response.StatusCode, model));
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new AsrConnectionTestResult(
                false,
                AsrConnectionStatusText.Failed((int)response.StatusCode, Truncate(body.Trim(), 300)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AsrConnectionTestResult(false, AsrConnectionStatusText.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
        {
            return new AsrConnectionTestResult(false, AsrConnectionStatusText.FailedWithMessage(ex.Message));
        }
    }

    /// <summary>把 /v1/audio/transcriptions 转成同源的 /v1/models（OpenAI 兼容惯例）。</summary>
    public static string BuildModelsUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        string trimmed = endpoint.Trim().TrimEnd('/');
        const string suffix = "/audio/transcriptions";
        int index = trimmed.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return trimmed[..index] + "/models";
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/models";
        }

        // 兜底：按 OpenAI 兼容惯例拼在末尾。
        var uri = new Uri(trimmed, UriKind.Absolute);
        return uri.AbsoluteUri.TrimEnd('/') + "/models";
    }

    private async Task<string> LoadSavedApiKeyAsync(CancellationToken ct)
    {
        var all = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        return DpapiCookieProtector.TryUnprotect(
            GetValue(all, AppSettingKeys.AsrEncryptedApiKey) ?? string.Empty,
            out var plain) ? plain : string.Empty;
    }

    private async Task SetOrDeleteAsync(string key, string? value, CancellationToken ct)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            await _settings.DeleteAsync(key, ct).ConfigureAwait(false);
        }
        else
        {
            await _settings.SetAsync(key, trimmed, ct).ConfigureAwait(false);
        }
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> all, string key)
        => all.TryGetValue(key, out var value) ? value : null;

    private static string NonEmptyOrFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
