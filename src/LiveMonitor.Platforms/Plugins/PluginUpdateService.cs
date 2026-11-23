using System.Text.Json;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>manifest 中发现的可用更新。</summary>
public sealed record PluginUpdateInfo(ProviderPluginEntry Entry, string? InstalledVersion);

/// <summary>更新检查失败项。</summary>
public sealed record PluginUpdateFailure(string PluginId, string ErrorMessage);

/// <summary>检查更新结果。</summary>
public sealed record PluginUpdateCheckResult(
    bool Succeeded,
    IReadOnlyList<PluginUpdateInfo> Updates,
    string? ManifestSource,
    string? ErrorMessage)
{
    public static PluginUpdateCheckResult Failed(string message)
        => new(false, Array.Empty<PluginUpdateInfo>(), null, message);
}

/// <summary>安装/应用更新结果。</summary>
public sealed record PluginUpdateResult(
    bool Succeeded,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Deferred,
    IReadOnlyList<PluginUpdateFailure> Failures,
    string? ErrorMessage);

/// <summary>
/// 平台插件在线更新服务（V0.5-B §4.2）。
/// 拉取 providers-manifest.json（主地址 + 镜像，按优先级）→ 解析/校验 → 与本地已安装/内置版本比较；
/// 下载 DLL 后交给 <see cref="PlatformPluginHost"/> 验签安装，再在无活跃任务时原子换表。
/// </summary>
public sealed class PluginUpdateService
{
    private const string UserAgent = "LimoClip-PluginUpdater/1.0.0";

    private readonly HttpClient _httpClient;
    private readonly PlatformPluginHost _host;
    private readonly string _currentAppVersion;
    private readonly IReadOnlyList<string> _manifestUrls;

    public PluginUpdateService(
        HttpClient httpClient,
        PlatformPluginHost host,
        string currentAppVersion,
        IReadOnlyList<string>? manifestUrls = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAppVersion);
        _currentAppVersion = currentAppVersion;
        _manifestUrls = manifestUrls ?? PluginTrust.ManifestUrls();
    }

    public IReadOnlyList<string> ManifestUrls => _manifestUrls;

    /// <summary>检查是否有可更新的插件；网络/解析失败返回 Succeeded=false 与中文原因（不抛异常）。</summary>
    public async Task<PluginUpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        ProvidersManifest? manifest = null;
        string? source = null;
        var failures = new List<string>();

        foreach (var url in _manifestUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{url} → HTTP {(int)response.StatusCode}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var parsed = ProvidersManifest.Parse(json);
                var errors = parsed.Validate();
                if (errors.Count > 0)
                {
                    failures.Add($"{url} → 校验失败：{string.Join("；", errors)}");
                    continue;
                }

                manifest = parsed;
                source = url;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or InvalidDataException or JsonException)
            {
                failures.Add($"{url} → {ex.Message}");
            }
        }

        if (manifest is null)
        {
            return PluginUpdateCheckResult.Failed(
                "无法获取平台插件清单：" + (failures.Count > 0 ? string.Join("；", failures) : "未配置更新源。"));
        }

        var updates = new List<PluginUpdateInfo>();
        foreach (var entry in manifest.Plugins)
        {
            if (PluginVersionComparer.IsNewer(entry.MinAppVersion, _currentAppVersion))
            {
                continue;
            }

            var installed = _host.GetInstalledEntry(entry.PluginId);
            var installedVersion = installed?.Version;
            if (installedVersion is not null && !PluginVersionComparer.IsNewer(entry.Version, installedVersion))
            {
                continue;
            }

            if (!_host.IsNewerThanBuiltIn(entry))
            {
                continue;
            }

            updates.Add(new PluginUpdateInfo(entry, installedVersion));
        }

        return new PluginUpdateCheckResult(true, updates, source, null);
    }

    /// <summary>下载并安装给定更新；随后尝试应用（有活跃任务时由宿主推迟到下次启动）。</summary>
    public async Task<PluginUpdateResult> InstallAsync(
        IReadOnlyList<PluginUpdateInfo> updates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var installed = new List<string>();
        var applied = new List<string>();
        var deferred = new List<string>();
        var failures = new List<PluginUpdateFailure>();

        foreach (var update in updates)
        {
            var entry = update.Entry;
            byte[] payload;
            try
            {
                payload = await _httpClient.GetByteArrayAsync(entry.DownloadUrl, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                failures.Add(new PluginUpdateFailure(entry.PluginId, $"下载插件失败：{ex.Message}"));
                continue;
            }

            if (!_host.TryInstall(entry, payload, out var installError))
            {
                failures.Add(new PluginUpdateFailure(entry.PluginId, installError ?? "安装插件失败。"));
                continue;
            }

            installed.Add(entry.PluginId);

            if (_host.TryApplyInstalledPlugin(entry.PluginId, out var applyError))
            {
                applied.Add(entry.PluginId);
            }
            else if (!string.IsNullOrWhiteSpace(applyError) && applyError.Contains("下次启动", StringComparison.Ordinal))
            {
                deferred.Add(entry.PluginId);
            }
            else
            {
                failures.Add(new PluginUpdateFailure(entry.PluginId, applyError ?? "应用插件失败。"));
            }
        }

        var succeeded = failures.Count == 0;
        string? error = succeeded ? null : $"有 {failures.Count} 个插件更新失败。";
        return new PluginUpdateResult(succeeded, installed, applied, deferred, failures, error);
    }
}
