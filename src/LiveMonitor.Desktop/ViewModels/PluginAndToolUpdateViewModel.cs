using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Platforms;
using LiveMonitor.Platforms.Plugins;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>更新分区状态文本的语气（决定前景色，由 View 映射为画刷）。</summary>
public enum UpdateStatusKind
{
    Neutral,
    Success,
    Error
}

/// <summary>
/// 设置页「组件与更新」子块：yt-dlp 与平台插件的版本检查、下载安装、忙状态与状态文案。
/// 四个更新动作以委托注入（生产环境由组合根里的 <see cref="CliUpdater"/> /
/// <see cref="PluginUpdateService"/> 提供），本类不引用任何 Avalonia UI 类型，
/// 便于在无 UI/无网络的单元测试中驱动。
/// </summary>
public sealed class PluginAndToolUpdateViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<CliUpdateCheckResult>> _checkYtDlpAsync;
    private readonly Func<CliUpdateCheckResult, CancellationToken, Task<CliUpdateResult>> _installYtDlpAsync;
    private readonly Func<CancellationToken, Task<PluginUpdateCheckResult>> _checkPluginUpdatesAsync;
    private readonly Func<IReadOnlyList<PluginUpdateInfo>, CancellationToken, Task<PluginUpdateResult>> _installPluginUpdatesAsync;

    /// <summary>最近一次 yt-dlp 检查结果（供「下载并更新」复用）。</summary>
    private CliUpdateCheckResult? _ytDlpUpdate;

    /// <summary>最近一次平台插件清单检查结果（供「下载并安装」复用）。</summary>
    private PluginUpdateCheckResult? _pluginUpdates;

    private bool _isBusy;
    private bool _canInstallYtDlp;
    private bool _canInstallPlugins;
    private string _ytDlpVersionText = PluginAndToolUpdateStatusText.YtDlpNotChecked;
    private string _pluginUpdateSummaryText = PluginAndToolUpdateStatusText.PluginsNotChecked;
    private string _statusText = PluginAndToolUpdateStatusText.Initial;
    private UpdateStatusKind _statusKind = UpdateStatusKind.Neutral;

    /// <summary>生产构造：直接复用组合根中的 yt-dlp 更新器与平台插件更新服务。</summary>
    public PluginAndToolUpdateViewModel(CliUpdater cliUpdater, PluginUpdateService pluginUpdates)
        : this(
            (cliUpdater ?? throw new ArgumentNullException(nameof(cliUpdater))).CheckYtDlpAsync,
            cliUpdater.DownloadAndInstallAsync,
            (pluginUpdates ?? throw new ArgumentNullException(nameof(pluginUpdates))).CheckAsync,
            pluginUpdates.InstallAsync)
    {
    }

    /// <summary>测试构造：把四个更新动作替换成委托，便于覆盖成功/失败/异常/重入分支。</summary>
    public PluginAndToolUpdateViewModel(
        Func<CancellationToken, Task<CliUpdateCheckResult>> checkYtDlpAsync,
        Func<CliUpdateCheckResult, CancellationToken, Task<CliUpdateResult>> installYtDlpAsync,
        Func<CancellationToken, Task<PluginUpdateCheckResult>> checkPluginUpdatesAsync,
        Func<IReadOnlyList<PluginUpdateInfo>, CancellationToken, Task<PluginUpdateResult>> installPluginUpdatesAsync)
    {
        _checkYtDlpAsync = checkYtDlpAsync ?? throw new ArgumentNullException(nameof(checkYtDlpAsync));
        _installYtDlpAsync = installYtDlpAsync ?? throw new ArgumentNullException(nameof(installYtDlpAsync));
        _checkPluginUpdatesAsync = checkPluginUpdatesAsync ?? throw new ArgumentNullException(nameof(checkPluginUpdatesAsync));
        _installPluginUpdatesAsync = installPluginUpdatesAsync
            ?? throw new ArgumentNullException(nameof(installPluginUpdatesAsync));
    }

    /// <summary>yt-dlp 版本行文案（本地/最新版本或检查/更新结果）。</summary>
    public string YtDlpVersionText
    {
        get => _ytDlpVersionText;
        private set => SetProperty(ref _ytDlpVersionText, value);
    }

    /// <summary>插件清单检查/安装结果摘要。</summary>
    public string PluginUpdateSummaryText
    {
        get => _pluginUpdateSummaryText;
        private set => SetProperty(ref _pluginUpdateSummaryText, value);
    }

    /// <summary>更新分区状态文案（前景色由 View 按 <see cref="StatusKind"/> 映射）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public UpdateStatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    /// <summary>检查/安装进行中（用于禁用检查按钮并阻止重入）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCheckYtDlp));
                OnPropertyChanged(nameof(CanCheckPlugins));
            }
        }
    }

    /// <summary>「检查更新」可用（任一更新操作进行中时禁用）。</summary>
    public bool CanCheckYtDlp => !IsBusy;

    /// <summary>「检查插件更新」可用（任一更新操作进行中时禁用）。</summary>
    public bool CanCheckPlugins => !IsBusy;

    /// <summary>yt-dlp「下载并更新」可用：检查到新版本且下载地址非空（与旧按钮 IsEnabled 赋值一一对应）。</summary>
    public bool CanInstallYtDlp
    {
        get => _canInstallYtDlp;
        private set => SetProperty(ref _canInstallYtDlp, value);
    }

    /// <summary>插件「下载并安装」可用：清单里存在可用更新。</summary>
    public bool CanInstallPlugins
    {
        get => _canInstallPlugins;
        private set => SetProperty(ref _canInstallPlugins, value);
    }

    /// <summary>检查 yt-dlp 是否有新版本（只查询、不下载）；异常收敛为错误状态，不向外抛。</summary>
    public async Task CheckYtDlpAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus(PluginAndToolUpdateStatusText.CheckingYtDlp, UpdateStatusKind.Neutral);
        try
        {
            var result = await _checkYtDlpAsync(ct).ConfigureAwait(true);
            _ytDlpUpdate = result;
            if (!result.CheckSucceeded)
            {
                YtDlpVersionText = PluginAndToolUpdateStatusText.CheckFailed;
                CanInstallYtDlp = false;
                SetStatus(PluginAndToolUpdateStatusText.YtDlpCheckFailed(result.ErrorMessage), UpdateStatusKind.Error);
                return;
            }

            YtDlpVersionText = PluginAndToolUpdateStatusText.YtDlpVersion(result.LocalVersion, result.LatestVersion);
            CanInstallYtDlp = result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl);
            SetStatus(
                result.UpdateAvailable
                    ? PluginAndToolUpdateStatusText.YtDlpUpdateAvailable(result.LatestVersion)
                    : PluginAndToolUpdateStatusText.YtDlpUpToDate,
                result.UpdateAvailable ? UpdateStatusKind.Neutral : UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            // 与旧实现一致：检查异常只禁用安装按钮，不清空上一次的检查结果。
            CanInstallYtDlp = false;
            SetStatus(PluginAndToolUpdateStatusText.YtDlpCheckFailed(ex.Message), UpdateStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载并原子替换 yt-dlp（体积与 MZ 头校验由服务完成）；只能在检查到可用更新后调用。</summary>
    public async Task InstallYtDlpAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var check = _ytDlpUpdate;
        if (check is null)
        {
            return;
        }

        if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            SetStatus(PluginAndToolUpdateStatusText.NoYtDlpUpdate, UpdateStatusKind.Error);
            return;
        }

        IsBusy = true;
        CanInstallYtDlp = false;
        SetStatus(PluginAndToolUpdateStatusText.DownloadingYtDlp, UpdateStatusKind.Neutral);
        try
        {
            var installed = await _installYtDlpAsync(check, ct).ConfigureAwait(true);
            if (!installed.Success)
            {
                SetStatus(PluginAndToolUpdateStatusText.YtDlpUpdateFailed(installed.ErrorMessage), UpdateStatusKind.Error);
                return;
            }

            YtDlpVersionText = PluginAndToolUpdateStatusText.YtDlpLocalUpdated(installed.NewVersion);
            _ytDlpUpdate = null;
            SetStatus(PluginAndToolUpdateStatusText.YtDlpUpdated(installed.NewVersion), UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus(PluginAndToolUpdateStatusText.YtDlpUpdateFailed(ex.Message), UpdateStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>拉取 providers-manifest.json（主源→镜像）并筛选可用插件更新；异常收敛为错误状态。</summary>
    public async Task CheckPluginUpdatesAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus(PluginAndToolUpdateStatusText.CheckingPlugins, UpdateStatusKind.Neutral);
        try
        {
            var result = await _checkPluginUpdatesAsync(ct).ConfigureAwait(true);
            _pluginUpdates = result;
            if (!result.Succeeded)
            {
                PluginUpdateSummaryText = PluginAndToolUpdateStatusText.CheckFailed;
                CanInstallPlugins = false;
                SetStatus(PluginAndToolUpdateStatusText.PluginCheckFailed(result.ErrorMessage), UpdateStatusKind.Error);
                return;
            }

            if (result.Updates.Count == 0)
            {
                PluginUpdateSummaryText = PluginAndToolUpdateStatusText.PluginLatest(result.ManifestSource);
                CanInstallPlugins = false;
                SetStatus(PluginAndToolUpdateStatusText.NoPluginUpdates, UpdateStatusKind.Success);
                return;
            }

            PluginUpdateSummaryText = string.Join(
                PluginAndToolUpdateStatusText.EntrySeparator,
                result.Updates.Select(u => PluginAndToolUpdateStatusText.PluginEntry(u.Entry.PluginId, u.Entry.Version)));
            CanInstallPlugins = true;
            SetStatus(
                PluginAndToolUpdateStatusText.PluginUpdatesFound(result.Updates.Count),
                UpdateStatusKind.Neutral);
        }
        catch (Exception ex)
        {
            CanInstallPlugins = false;
            SetStatus(PluginAndToolUpdateStatusText.PluginCheckFailed(ex.Message), UpdateStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载并安装插件：验签通过后在无录制/弹幕任务时立即生效，否则下次启动生效。</summary>
    public async Task InstallPluginUpdatesAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var check = _pluginUpdates;
        if (check is null || check.Updates.Count == 0)
        {
            return;
        }

        IsBusy = true;
        CanInstallPlugins = false;
        SetStatus(PluginAndToolUpdateStatusText.DownloadingPlugins, UpdateStatusKind.Neutral);
        try
        {
            var result = await _installPluginUpdatesAsync(check.Updates, ct).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                SetStatus(PluginAndToolUpdateStatusText.PluginInstallFailed(result.ErrorMessage), UpdateStatusKind.Error);
                return;
            }

            var parts = new List<string>();
            if (result.Installed.Count > 0)
            {
                parts.Add(PluginAndToolUpdateStatusText.PluginInstalled(result.Installed.Count));
            }

            if (result.Applied.Count > 0)
            {
                parts.Add(PluginAndToolUpdateStatusText.PluginApplied(result.Applied.Count));
            }

            if (result.Deferred.Count > 0)
            {
                parts.Add(PluginAndToolUpdateStatusText.PluginDeferred(result.Deferred.Count));
            }

            if (result.Failures.Count > 0)
            {
                parts.Add(PluginAndToolUpdateStatusText.PluginFailures(
                    result.Failures.Count,
                    string.Join(
                        PluginAndToolUpdateStatusText.EntrySeparator,
                        result.Failures.Select(f => PluginAndToolUpdateStatusText.PluginFailureDetail(f.PluginId, f.ErrorMessage)))));
            }

            var summary = parts.Count > 0
                ? string.Join(PluginAndToolUpdateStatusText.PartSeparator, parts)
                : PluginAndToolUpdateStatusText.NoPluginChanges;
            PluginUpdateSummaryText = summary;
            _pluginUpdates = null;
            SetStatus(
                parts.Count > 0 ? PluginAndToolUpdateStatusText.PluginUpdateCompleted(summary) : PluginAndToolUpdateStatusText.NoPluginUpdateToInstall,
                result.Failures.Count > 0 ? UpdateStatusKind.Error : UpdateStatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus(PluginAndToolUpdateStatusText.PluginInstallFailed(ex.Message), UpdateStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>更新分区状态文本并按语气记录类型（画刷由 View 映射）。</summary>
    private void SetStatus(string text, UpdateStatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }
}
