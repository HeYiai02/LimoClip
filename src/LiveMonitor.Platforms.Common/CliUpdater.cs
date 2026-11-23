using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace LiveMonitor.Platforms;

/// <summary>一次 CLI 组件更新检查的结果。</summary>
public sealed record CliUpdateCheckResult(
    bool CheckSucceeded,
    string? LocalVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? ErrorMessage);

/// <summary>CLI 组件下载/替换结果。</summary>
public sealed record CliUpdateResult(bool Success, string? NewVersion, string? ErrorMessage);

/// <summary>
/// 外部命令行组件（当前为 yt-dlp）的更新器（V0.5-B §4.3）。
/// 流程：读本地 --version → 对比 GitHub latest tag → 下载新 exe 到 .tmp → 校验 PE 头 → 原子替换。
/// 只做检查与替换，不负责 UI；由设置页「组件与插件更新」分区调用。
/// </summary>
public sealed class CliUpdater
{
    public const string YtDlpLatestReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    private const string UserAgent = "LimoClip-CliUpdater/1.0.0";
    private const int MinimumExecutableBytes = 1024;

    private readonly string _executablePath;
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<string?>> _versionReader;
    private readonly string _tempDirectory;

    public CliUpdater(
        string executablePath,
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<string?>>? versionReader = null,
        string? tempDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _executablePath = Path.GetFullPath(executablePath);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _versionReader = versionReader ?? ReadVersionAsync;
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "LimoClip");
    }

    /// <summary>被管理的可执行文件绝对路径。</summary>
    public string ExecutablePath => _executablePath;

    /// <summary>检查 yt-dlp 是否有新版本；不下载、不修改文件。</summary>
    public async Task<CliUpdateCheckResult> CheckYtDlpAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_executablePath))
        {
            return new CliUpdateCheckResult(false, null, null, false, null, $"找不到 yt-dlp.exe：{_executablePath}");
        }

        string? localVersion;
        try
        {
            localVersion = await _versionReader(_executablePath, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CliUpdateCheckResult(false, null, null, false, null, $"读取本地 yt-dlp 版本失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(localVersion))
        {
            return new CliUpdateCheckResult(false, null, null, false, null, "无法读取本地 yt-dlp 版本（--version 无输出）。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, YtDlpLatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new CliUpdateCheckResult(
                    false, localVersion, null, false, null,
                    $"检查 yt-dlp 更新失败：GitHub 返回 {(int)response.StatusCode}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var latestVersion = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new CliUpdateCheckResult(false, localVersion, null, false, null, "GitHub 最新版本信息缺少 tag_name。");
            }

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.Equals(name, "yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    break;
                }
            }

            var updateAvailable = PluginVersionComparer.IsNewer(latestVersion, localVersion);
            var error = updateAvailable && string.IsNullOrWhiteSpace(downloadUrl)
                ? "GitHub Release 中未找到 yt-dlp.exe 资产。"
                : null;
            return new CliUpdateCheckResult(true, localVersion, latestVersion, updateAvailable, downloadUrl, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new CliUpdateCheckResult(false, localVersion, null, false, null, $"检查 yt-dlp 更新失败：{ex.Message}");
        }
    }

    /// <summary>下载并原子替换 yt-dlp.exe；替换前校验体积与 PE 头（MZ）。</summary>
    public async Task<CliUpdateResult> DownloadAndInstallAsync(
        CliUpdateCheckResult check,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            return new CliUpdateResult(false, check.LatestVersion, check.ErrorMessage ?? "没有可下载的 yt-dlp 更新地址。");
        }

        byte[] payload;
        try
        {
            payload = await _httpClient.GetByteArrayAsync(check.DownloadUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new CliUpdateResult(false, check.LatestVersion, $"下载 yt-dlp.exe 失败：{ex.Message}");
        }

        if (payload.Length < MinimumExecutableBytes)
        {
            return new CliUpdateResult(false, check.LatestVersion, "下载的 yt-dlp.exe 体积异常（可能不是可执行文件）。");
        }

        if (payload[0] != (byte)'M' || payload[1] != (byte)'Z')
        {
            return new CliUpdateResult(false, check.LatestVersion, "下载内容不是有效的 Windows 可执行文件（缺少 MZ 头）。");
        }

        var temporaryPath = _executablePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_executablePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(temporaryPath, payload, ct);
            File.Move(temporaryPath, _executablePath, overwrite: true);
            return new CliUpdateResult(true, check.LatestVersion, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return new CliUpdateResult(false, check.LatestVersion, $"替换 yt-dlp.exe 失败：{ex.Message}");
        }
    }

    private async Task<string?> ReadVersionAsync(string executablePath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_tempDirectory);
            var result = await Cli.Wrap(executablePath)
                .WithArguments("--version")
                .WithEnvironmentVariables(new Dictionary<string, string?>
                {
                    ["TEMP"] = _tempDirectory,
                    ["TMP"] = _tempDirectory,
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
            {
                return null;
            }

            var output = result.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(output)
                ? null
                : output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响结果。
        }
    }
}
