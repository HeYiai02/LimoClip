using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>「组件与更新」分区状态文案的逐字与拼接规则（表单行为由 PluginAndToolUpdateViewModelTests 覆盖）。</summary>
public sealed class PluginAndToolUpdateStatusTextTests
{
    [Fact]
    public void Constants_keep_original_copy()
    {
        Assert.Equal("尚未检查", PluginAndToolUpdateStatusText.YtDlpNotChecked);
        Assert.Equal("尚未检查（内置 B 站 / Twitch / YouTube / 虎牙 / 斗鱼 / 抖音）",
            PluginAndToolUpdateStatusText.PluginsNotChecked);
        Assert.Equal("更新组件会从 GitHub 下载，需要联网。", PluginAndToolUpdateStatusText.Initial);
        Assert.Equal("检查失败", PluginAndToolUpdateStatusText.CheckFailed);
        Assert.Equal("正在检查 yt-dlp 更新…", PluginAndToolUpdateStatusText.CheckingYtDlp);
        Assert.Equal("正在下载并替换 yt-dlp…", PluginAndToolUpdateStatusText.DownloadingYtDlp);
        Assert.Equal("yt-dlp 已是最新版本。", PluginAndToolUpdateStatusText.YtDlpUpToDate);
        Assert.Equal("没有可用的 yt-dlp 更新，请先点击「检查更新」。", PluginAndToolUpdateStatusText.NoYtDlpUpdate);
        Assert.Equal("正在检查平台插件更新…", PluginAndToolUpdateStatusText.CheckingPlugins);
        Assert.Equal("没有可用的平台插件更新。", PluginAndToolUpdateStatusText.NoPluginUpdates);
        Assert.Equal("正在下载并安装平台插件…", PluginAndToolUpdateStatusText.DownloadingPlugins);
        Assert.Equal("没有变化", PluginAndToolUpdateStatusText.NoPluginChanges);
        Assert.Equal("没有可安装的插件更新。", PluginAndToolUpdateStatusText.NoPluginUpdateToInstall);
        Assert.Equal("；", PluginAndToolUpdateStatusText.EntrySeparator);
        Assert.Equal("，", PluginAndToolUpdateStatusText.PartSeparator);
    }

    [Fact]
    public void YtDlpVersion_pins_local_and_latest()
    {
        Assert.Equal("本地 2024.01.01 · 最新 2024.02.02",
            PluginAndToolUpdateStatusText.YtDlpVersion("2024.01.01", "2024.02.02"));
        // null 走插值即空串；生产调用点只会在 CheckSucceeded 后传入两端版本。
        Assert.Equal("本地  · 最新 ", PluginAndToolUpdateStatusText.YtDlpVersion(null, ""));
    }

    [Fact]
    public void YtDlpLocalUpdated_pins_updated_marker()
        => Assert.Equal("本地 2024.02.02 · 已更新", PluginAndToolUpdateStatusText.YtDlpLocalUpdated("2024.02.02"));

    [Fact]
    public void YtDlpUpdateAvailable_mentions_tools_directory()
    {
        var text = PluginAndToolUpdateStatusText.YtDlpUpdateAvailable("2024.02.02");

        Assert.Equal("发现新版本 2024.02.02，点击「下载并更新」替换 tools 目录内的 yt-dlp.exe。", text);
        Assert.Contains("tools", text);
        Assert.Contains("yt-dlp.exe", text);
    }

    [Fact]
    public void FailureTemplates_keep_prefix_and_message()
    {
        Assert.Equal("检查 yt-dlp 更新失败：网络不通", PluginAndToolUpdateStatusText.YtDlpCheckFailed("网络不通"));
        Assert.Equal("更新 yt-dlp 失败：文件被占用", PluginAndToolUpdateStatusText.YtDlpUpdateFailed("文件被占用"));
        Assert.Equal("检查插件更新失败：清单 404", PluginAndToolUpdateStatusText.PluginCheckFailed("清单 404"));
        Assert.Equal("安装平台插件失败：磁盘已满", PluginAndToolUpdateStatusText.PluginInstallFailed("磁盘已满"));
    }

    [Fact]
    public void YtDlpUpdated_ends_with_period()
        => Assert.Equal("yt-dlp 已更新到 2024.02.02。", PluginAndToolUpdateStatusText.YtDlpUpdated("2024.02.02"));

    [Fact]
    public void PluginLatest_includes_manifest_source()
        => Assert.Equal("已是最新（清单来源：https://a/manifest.json）",
            PluginAndToolUpdateStatusText.PluginLatest("https://a/manifest.json"));

    [Fact]
    public void PluginEntry_joins_id_and_version()
        => Assert.Equal("huya 1.2.0", PluginAndToolUpdateStatusText.PluginEntry("huya", "1.2.0"));

    [Fact]
    public void PluginUpdatesFound_includes_count()
    {
        var text = PluginAndToolUpdateStatusText.PluginUpdatesFound(3);

        Assert.Equal("发现 3 个插件更新，点击「下载并安装」（下载后自动验签）。", text);
        Assert.Contains("3", text);
        Assert.Contains("验签", text);
    }

    [Fact]
    public void InstallParts_use_counted_phrases()
    {
        Assert.Equal("已安装 2 个", PluginAndToolUpdateStatusText.PluginInstalled(2));
        Assert.Equal("已生效 1 个", PluginAndToolUpdateStatusText.PluginApplied(1));
        Assert.Equal("待下次启动生效 4 个", PluginAndToolUpdateStatusText.PluginDeferred(4));
    }

    [Fact]
    public void PluginFailures_combines_count_and_details()
    {
        var details = string.Join(
            PluginAndToolUpdateStatusText.EntrySeparator,
            PluginAndToolUpdateStatusText.PluginFailureDetail("huya", "验签失败"),
            PluginAndToolUpdateStatusText.PluginFailureDetail("douyu", "下载超时"));

        Assert.Equal("huya（验签失败）；douyu（下载超时）", details);
        Assert.Equal("失败 2 个：huya（验签失败）；douyu（下载超时）",
            PluginAndToolUpdateStatusText.PluginFailures(2, details));
    }

    [Fact]
    public void PluginUpdateCompleted_wraps_summary_with_period()
        => Assert.Equal("插件更新完成：已安装 1 个。", PluginAndToolUpdateStatusText.PluginUpdateCompleted("已安装 1 个"));
}
