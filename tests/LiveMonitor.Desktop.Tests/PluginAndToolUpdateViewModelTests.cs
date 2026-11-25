using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 设置页「组件与更新」子块 ViewModel 的无 UI/无网络单测：四个更新动作由可编程委托替换，
/// 覆盖检查/安装的成功、失败、异常、忙碌防重入、版本文案与插件列表摘要分支。
/// 无法在此覆盖、需真机冒烟验证的部分：AXAML 控件绑定是否生效（含 CanCheck*/CanInstall* 的 IsEnabled）、
/// 状态文本画刷（View 按 StatusKind 映射）、两个 Click 转发入口的 sender 分派、UpdatesPage 的
/// DataContext 挂载时机。
/// </summary>
public sealed class PluginAndToolUpdateViewModelTests
{
    private const string ManifestSource = "https://example.com/providers-manifest.json";

    [Fact]
    public void Initial_state_shows_placeholders_and_disables_install_buttons()
    {
        var (vm, fakes) = CreateViewModel();

        Assert.Equal("尚未检查", vm.YtDlpVersionText);
        Assert.Equal("尚未检查（内置 B 站 / Twitch / YouTube / 虎牙 / 斗鱼 / 抖音）", vm.PluginUpdateSummaryText);
        Assert.Equal("更新组件会从 GitHub 下载，需要联网。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
        Assert.True(vm.CanCheckPlugins);
        Assert.False(vm.CanInstallYtDlp);
        Assert.False(vm.CanInstallPlugins);
        Assert.Equal(0, fakes.TotalCalls);
    }

    [Fact]
    public async Task CheckYtDlpAsync_with_update_available_shows_versions_and_enables_install()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true));
        var propertyChanges = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        await vm.CheckYtDlpAsync();

        Assert.Equal(1, fakes.CheckYtDlpCalls);
        Assert.Equal("本地 2024.01.01 · 最新 2024.02.02", vm.YtDlpVersionText);
        Assert.True(vm.CanInstallYtDlp);
        Assert.Equal(
            "发现新版本 2024.02.02，点击「下载并更新」替换 tools 目录内的 yt-dlp.exe。",
            vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
        Assert.Contains(nameof(PluginAndToolUpdateViewModel.CanCheckYtDlp), propertyChanges);
        Assert.Contains(nameof(PluginAndToolUpdateViewModel.CanInstallYtDlp), propertyChanges);
    }

    [Fact]
    public async Task CheckYtDlpAsync_when_latest_shows_success_and_keeps_install_disabled()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: false));

        await vm.CheckYtDlpAsync();

        Assert.Equal("本地 2024.01.01 · 最新 2024.02.02", vm.YtDlpVersionText);
        Assert.False(vm.CanInstallYtDlp);
        Assert.Equal("yt-dlp 已是最新版本。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Success, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckYtDlpAsync_failure_result_shows_failed_version_and_error()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(error: "GitHub 返回 403"));

        await vm.CheckYtDlpAsync();

        Assert.Equal("检查失败", vm.YtDlpVersionText);
        Assert.False(vm.CanInstallYtDlp);
        Assert.Equal("检查 yt-dlp 更新失败：GitHub 返回 403", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
    }

    [Fact]
    public async Task CheckYtDlpAsync_update_without_download_url_keeps_install_disabled()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true, downloadUrl: false));

        await vm.CheckYtDlpAsync();

        Assert.Equal(1, fakes.CheckYtDlpCalls);
        // 与旧实现一致：有更新但缺下载地址时仍提示「发现新版本」，只是不启用安装按钮。
        Assert.False(vm.CanInstallYtDlp);
        Assert.Contains("发现新版本 2024.02.02", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
    }

    [Fact]
    public async Task CheckYtDlpAsync_exception_reports_error_and_keeps_version_text()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => throw new HttpRequestException("网络不可达");

        await vm.CheckYtDlpAsync();

        Assert.Equal("检查 yt-dlp 更新失败：网络不可达", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.Equal("尚未检查", vm.YtDlpVersionText);
        Assert.False(vm.CanInstallYtDlp);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckYtDlpAsync_blocks_reentry_while_busy()
    {
        var (vm, fakes) = CreateViewModel();
        var entered = NewSignal();
        var release = NewSignal();
        fakes.YtDlpCheckHandler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return YtDlpCheck(updateAvailable: true);
        };

        var first = vm.CheckYtDlpAsync();
        await entered.Task;

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCheckYtDlp);
        Assert.False(vm.CanCheckPlugins);

        await vm.CheckYtDlpAsync(); // 重入直接返回，不触达服务
        Assert.Equal(1, fakes.CheckYtDlpCalls);

        release.TrySetResult();
        await first;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
        Assert.True(vm.CanCheckPlugins);
        Assert.True(vm.CanInstallYtDlp);
    }

    [Fact]
    public async Task InstallYtDlpAsync_without_check_is_silent_noop()
    {
        var (vm, fakes) = CreateViewModel();

        await vm.InstallYtDlpAsync();

        Assert.Equal(0, fakes.InstallYtDlpCalls);
        Assert.Equal("更新组件会从 GitHub 下载，需要联网。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task InstallYtDlpAsync_rejects_check_without_download_url()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true, downloadUrl: false));
        await vm.CheckYtDlpAsync();

        await vm.InstallYtDlpAsync();

        Assert.Equal(0, fakes.InstallYtDlpCalls);
        Assert.Equal("没有可用的 yt-dlp 更新，请先点击「检查更新」。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task InstallYtDlpAsync_success_reports_version_and_resets_pending_check()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true));
        fakes.YtDlpInstallHandler = (_, _) => Task.FromResult(new CliUpdateResult(true, "2024.03.03", null));
        await vm.CheckYtDlpAsync();

        await vm.InstallYtDlpAsync();

        Assert.Equal(1, fakes.InstallYtDlpCalls);
        Assert.Equal("2024.01.01", fakes.InstalledYtDlpCheck!.LocalVersion);
        Assert.Equal("本地 2024.03.03 · 已更新", vm.YtDlpVersionText);
        Assert.Equal("yt-dlp 已更新到 2024.03.03。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Success, vm.StatusKind);
        Assert.False(vm.CanInstallYtDlp);
        Assert.False(vm.IsBusy);

        await vm.InstallYtDlpAsync(); // 检查结果已清空：静默 no-op
        Assert.Equal(1, fakes.InstallYtDlpCalls);
    }

    [Fact]
    public async Task InstallYtDlpAsync_failure_result_reports_error()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true));
        fakes.YtDlpInstallHandler = (_, _) => Task.FromResult(new CliUpdateResult(false, null, "替换被占用"));
        await vm.CheckYtDlpAsync();

        await vm.InstallYtDlpAsync();

        Assert.Equal("更新 yt-dlp 失败：替换被占用", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
    }

    [Fact]
    public async Task InstallYtDlpAsync_exception_reports_error_and_restores_busy()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true));
        fakes.YtDlpInstallHandler = (_, _) => throw new IOException("磁盘写入失败");
        await vm.CheckYtDlpAsync();

        await vm.InstallYtDlpAsync();

        Assert.Equal("更新 yt-dlp 失败：磁盘写入失败", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
    }

    [Fact]
    public async Task InstallYtDlpAsync_blocks_reentry_and_disables_install_while_pending()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.YtDlpCheckHandler = _ => Task.FromResult(YtDlpCheck(updateAvailable: true));
        await vm.CheckYtDlpAsync();

        var entered = NewSignal();
        var release = NewSignal();
        fakes.YtDlpInstallHandler = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new CliUpdateResult(true, "2024.03.03", null);
        };

        var first = vm.InstallYtDlpAsync();
        await entered.Task;

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCheckYtDlp);
        Assert.False(vm.CanInstallYtDlp);

        await vm.InstallYtDlpAsync();
        Assert.Equal(1, fakes.InstallYtDlpCalls);

        release.TrySetResult();
        await first;

        Assert.False(vm.IsBusy);
        Assert.Equal("yt-dlp 已更新到 2024.03.03。", vm.StatusText);
    }

    [Fact]
    public async Task CheckPluginUpdatesAsync_failure_result_shows_error_summary()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginUpdateCheckResult.Failed("manifest 404"));

        await vm.CheckPluginUpdatesAsync();

        Assert.Equal("检查失败", vm.PluginUpdateSummaryText);
        Assert.False(vm.CanInstallPlugins);
        Assert.Equal("检查插件更新失败：manifest 404", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckPluginUpdatesAsync_no_updates_reports_manifest_source()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck());

        await vm.CheckPluginUpdatesAsync();

        Assert.Equal($"已是最新（清单来源：{ManifestSource}）", vm.PluginUpdateSummaryText);
        Assert.False(vm.CanInstallPlugins);
        Assert.Equal("没有可用的平台插件更新。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task CheckPluginUpdatesAsync_with_updates_lists_versions_and_enables_install()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(
            PluginUpdate("bilibili", "1.2.0"),
            PluginUpdate("twitch", "1.3.0")));

        await vm.CheckPluginUpdatesAsync();

        Assert.Equal("bilibili 1.2.0；twitch 1.3.0", vm.PluginUpdateSummaryText);
        Assert.True(vm.CanInstallPlugins);
        Assert.Equal("发现 2 个插件更新，点击「下载并安装」（下载后自动验签）。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
        Assert.True(vm.CanCheckPlugins);
    }

    [Fact]
    public async Task CheckPluginUpdatesAsync_exception_reports_error_and_keeps_summary()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => throw new HttpRequestException("清单下载超时");

        await vm.CheckPluginUpdatesAsync();

        Assert.Equal("检查插件更新失败：清单下载超时", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.Equal("尚未检查（内置 B 站 / Twitch / YouTube / 虎牙 / 斗鱼 / 抖音）", vm.PluginUpdateSummaryText);
        Assert.False(vm.CanInstallPlugins);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckPluginUpdatesAsync_blocks_reentry_while_busy()
    {
        var (vm, fakes) = CreateViewModel();
        var entered = NewSignal();
        var release = NewSignal();
        fakes.PluginCheckHandler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return PluginCheck();
        };

        var first = vm.CheckPluginUpdatesAsync();
        await entered.Task;

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCheckPlugins);
        Assert.False(vm.CanCheckYtDlp);

        await vm.CheckPluginUpdatesAsync();
        Assert.Equal(1, fakes.CheckPluginCalls);

        release.TrySetResult();
        await first;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckPlugins);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_without_check_is_silent_noop()
    {
        var (vm, fakes) = CreateViewModel();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal(0, fakes.InstallPluginCalls);
        Assert.Equal("更新组件会从 GitHub 下载，需要联网。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Neutral, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_success_reports_parts_and_resets_pending_check()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(
            PluginUpdate("bilibili", "1.2.0"),
            PluginUpdate("twitch", "1.3.0")));
        fakes.PluginInstallHandler = (_, _) => Task.FromResult(InstallResult(
            installed: new[] { "bilibili", "twitch" },
            applied: new[] { "bilibili" },
            deferred: new[] { "twitch" }));
        await vm.CheckPluginUpdatesAsync();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal(1, fakes.InstallPluginCalls);
        Assert.Equal(2, fakes.InstalledPluginUpdates!.Count);
        Assert.Equal("已安装 2 个，已生效 1 个，待下次启动生效 1 个", vm.PluginUpdateSummaryText);
        Assert.Equal("插件更新完成：已安装 2 个，已生效 1 个，待下次启动生效 1 个。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Success, vm.StatusKind);
        Assert.False(vm.CanInstallPlugins);
        Assert.False(vm.IsBusy);

        await vm.InstallPluginUpdatesAsync(); // 检查结果已清空：静默 no-op
        Assert.Equal(1, fakes.InstallPluginCalls);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_failures_report_error_kind_and_details()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(
            PluginUpdate("bilibili", "1.2.0"),
            PluginUpdate("twitch", "1.3.0")));
        fakes.PluginInstallHandler = (_, _) => Task.FromResult(InstallResult(
            installed: new[] { "bilibili" },
            failures: new[] { new PluginUpdateFailure("twitch", "验签失败") }));
        await vm.CheckPluginUpdatesAsync();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal("已安装 1 个，失败 1 个：twitch（验签失败）", vm.PluginUpdateSummaryText);
        Assert.Equal("插件更新完成：已安装 1 个，失败 1 个：twitch（验签失败）。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_empty_success_reports_no_changes()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(PluginUpdate("bilibili", "1.2.0")));
        fakes.PluginInstallHandler = (_, _) => Task.FromResult(InstallResult());
        await vm.CheckPluginUpdatesAsync();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal("没有变化", vm.PluginUpdateSummaryText);
        Assert.Equal("没有可安装的插件更新。", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_service_failure_reports_error()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(PluginUpdate("bilibili", "1.2.0")));
        fakes.PluginInstallHandler = (_, _) => Task.FromResult(InstallResult(error: "下载失败"));
        await vm.CheckPluginUpdatesAsync();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal("安装平台插件失败：下载失败", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
        // 失败时保留上次检查结果（与旧实现一致），但安装按钮已被禁用。
        Assert.False(vm.CanInstallPlugins);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_exception_reports_error_and_restores_busy()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(PluginUpdate("bilibili", "1.2.0")));
        fakes.PluginInstallHandler = (_, _) => throw new IOException("插件目录被占用");
        await vm.CheckPluginUpdatesAsync();

        await vm.InstallPluginUpdatesAsync();

        Assert.Equal("安装平台插件失败：插件目录被占用", vm.StatusText);
        Assert.Equal(UpdateStatusKind.Error, vm.StatusKind);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanCheckYtDlp);
    }

    [Fact]
    public async Task InstallPluginUpdatesAsync_blocks_reentry_while_busy()
    {
        var (vm, fakes) = CreateViewModel();
        fakes.PluginCheckHandler = _ => Task.FromResult(PluginCheck(PluginUpdate("bilibili", "1.2.0")));
        await vm.CheckPluginUpdatesAsync();

        var entered = NewSignal();
        var release = NewSignal();
        fakes.PluginInstallHandler = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return InstallResult(installed: new[] { "bilibili" });
        };

        var first = vm.InstallPluginUpdatesAsync();
        await entered.Task;

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCheckPlugins);
        Assert.False(vm.CanInstallPlugins);

        await vm.InstallPluginUpdatesAsync();
        Assert.Equal(1, fakes.InstallPluginCalls);

        release.TrySetResult();
        await first;

        Assert.False(vm.IsBusy);
        Assert.Equal("插件更新完成：已安装 1 个。", vm.StatusText);
    }

    private static (PluginAndToolUpdateViewModel Vm, FakeUpdateActions Fakes) CreateViewModel()
    {
        var fakes = new FakeUpdateActions();
        return (fakes.CreateViewModel(), fakes);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CliUpdateCheckResult YtDlpCheck(
        bool updateAvailable = false,
        bool downloadUrl = true,
        string local = "2024.01.01",
        string latest = "2024.02.02",
        string? error = null)
        => new(
            CheckSucceeded: error is null,
            LocalVersion: local,
            LatestVersion: latest,
            UpdateAvailable: updateAvailable,
            DownloadUrl: updateAvailable && downloadUrl ? "https://example.com/yt-dlp.exe" : null,
            ErrorMessage: error);

    private static PluginUpdateCheckResult PluginCheck(params PluginUpdateInfo[] updates)
        => new(true, updates, ManifestSource, null);

    private static PluginUpdateInfo PluginUpdate(string pluginId, string version)
        => new(
            new ProviderPluginEntry
            {
                PluginId = pluginId,
                Platform = PlatformType.Bilibili,
                Version = version,
                DownloadUrl = "https://example.com/plugin.dll",
            },
            InstalledVersion: null);

    private static PluginUpdateResult InstallResult(
        string[]? installed = null,
        string[]? applied = null,
        string[]? deferred = null,
        PluginUpdateFailure[]? failures = null,
        string? error = null)
    {
        var failureList = failures ?? Array.Empty<PluginUpdateFailure>();
        return new PluginUpdateResult(
            error is null,
            installed ?? Array.Empty<string>(),
            applied ?? Array.Empty<string>(),
            deferred ?? Array.Empty<string>(),
            failureList,
            error);
    }

    /// <summary>可编程的四个更新动作：设置返回值/异常或挂起（TCS）即可覆盖忙碌与重入分支。</summary>
    private sealed class FakeUpdateActions
    {
        public Func<CancellationToken, Task<CliUpdateCheckResult>> YtDlpCheckHandler { get; set; } =
            _ => Task.FromResult(YtDlpCheck());

        public Func<CliUpdateCheckResult, CancellationToken, Task<CliUpdateResult>> YtDlpInstallHandler { get; set; } =
            (check, _) => Task.FromResult(new CliUpdateResult(true, check.LatestVersion, null));

        public Func<CancellationToken, Task<PluginUpdateCheckResult>> PluginCheckHandler { get; set; } =
            _ => Task.FromResult(PluginCheck());

        public Func<IReadOnlyList<PluginUpdateInfo>, CancellationToken, Task<PluginUpdateResult>> PluginInstallHandler { get; set; } =
            (_, _) => Task.FromResult(InstallResult());

        public int CheckYtDlpCalls { get; private set; }

        public int InstallYtDlpCalls { get; private set; }

        public int CheckPluginCalls { get; private set; }

        public int InstallPluginCalls { get; private set; }

        public int TotalCalls => CheckYtDlpCalls + InstallYtDlpCalls + CheckPluginCalls + InstallPluginCalls;

        public CliUpdateCheckResult? InstalledYtDlpCheck { get; private set; }

        public IReadOnlyList<PluginUpdateInfo>? InstalledPluginUpdates { get; private set; }

        public PluginAndToolUpdateViewModel CreateViewModel()
            => new(CheckYtDlpCoreAsync, InstallYtDlpCoreAsync, CheckPluginsCoreAsync, InstallPluginsCoreAsync);

        private Task<CliUpdateCheckResult> CheckYtDlpCoreAsync(CancellationToken ct)
        {
            CheckYtDlpCalls++;
            return YtDlpCheckHandler(ct);
        }

        private Task<CliUpdateResult> InstallYtDlpCoreAsync(CliUpdateCheckResult check, CancellationToken ct)
        {
            InstallYtDlpCalls++;
            InstalledYtDlpCheck = check;
            return YtDlpInstallHandler(check, ct);
        }

        private Task<PluginUpdateCheckResult> CheckPluginsCoreAsync(CancellationToken ct)
        {
            CheckPluginCalls++;
            return PluginCheckHandler(ct);
        }

        private Task<PluginUpdateResult> InstallPluginsCoreAsync(
            IReadOnlyList<PluginUpdateInfo> updates,
            CancellationToken ct)
        {
            InstallPluginCalls++;
            InstalledPluginUpdates = updates;
            return PluginInstallHandler(updates, ct);
        }
    }
}
