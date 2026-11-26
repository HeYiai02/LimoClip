namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 设置页「组件与更新」分区的版本行与状态提示文案（V0.7.5）。
/// 仅负责拼装字符串，检查/安装流程与状态切换仍由 <see cref="PluginAndToolUpdateViewModel"/> 编排。
/// </summary>
internal static class PluginAndToolUpdateStatusText
{
    /// <summary>yt-dlp 版本行初始占位。</summary>
    public const string YtDlpNotChecked = "尚未检查";

    /// <summary>插件摘要初始占位（含内置平台列表）。</summary>
    public const string PluginsNotChecked = "尚未检查（内置 B 站 / Twitch / YouTube / 虎牙 / 斗鱼 / 抖音）";

    /// <summary>分区初始状态提示。</summary>
    public const string Initial = "更新组件会从 GitHub 下载，需要联网。";

    /// <summary>检查失败时版本/摘要行的短提示。</summary>
    public const string CheckFailed = "检查失败";

    public const string CheckingYtDlp = "正在检查 yt-dlp 更新…";
    public const string DownloadingYtDlp = "正在下载并替换 yt-dlp…";
    public const string YtDlpUpToDate = "yt-dlp 已是最新版本。";
    public const string NoYtDlpUpdate = "没有可用的 yt-dlp 更新，请先点击「检查更新」。";
    public const string CheckingPlugins = "正在检查平台插件更新…";
    public const string NoPluginUpdates = "没有可用的平台插件更新。";
    public const string DownloadingPlugins = "正在下载并安装平台插件…";
    public const string NoPluginChanges = "没有变化";
    public const string NoPluginUpdateToInstall = "没有可安装的插件更新。";

    /// <summary>插件条目之间的分隔符。</summary>
    public const string EntrySeparator = "；";

    /// <summary>安装摘要各段之间的分隔符。</summary>
    public const string PartSeparator = "，";

    public static string YtDlpVersion(string? localVersion, string? latestVersion)
        => $"本地 {localVersion} · 最新 {latestVersion}";

    public static string YtDlpLocalUpdated(string? newVersion)
        => $"本地 {newVersion} · 已更新";

    public static string YtDlpUpdateAvailable(string? latestVersion)
        => $"发现新版本 {latestVersion}，点击「下载并更新」替换 tools 目录内的 yt-dlp.exe。";

    public static string YtDlpCheckFailed(string? message)
        => $"检查 yt-dlp 更新失败：{message}";

    public static string YtDlpUpdateFailed(string? message)
        => $"更新 yt-dlp 失败：{message}";

    public static string YtDlpUpdated(string? newVersion)
        => $"yt-dlp 已更新到 {newVersion}。";

    public static string PluginLatest(string? manifestSource)
        => $"已是最新（清单来源：{manifestSource}）";

    public static string PluginEntry(string pluginId, string version)
        => $"{pluginId} {version}";

    public static string PluginUpdatesFound(int count)
        => $"发现 {count} 个插件更新，点击「下载并安装」（下载后自动验签）。";

    public static string PluginCheckFailed(string? message)
        => $"检查插件更新失败：{message}";

    public static string PluginInstallFailed(string? message)
        => $"安装平台插件失败：{message}";

    public static string PluginInstalled(int count) => $"已安装 {count} 个";

    public static string PluginApplied(int count) => $"已生效 {count} 个";

    public static string PluginDeferred(int count) => $"待下次启动生效 {count} 个";

    public static string PluginFailureDetail(string pluginId, string? errorMessage)
        => $"{pluginId}（{errorMessage}）";

    public static string PluginFailures(int count, string details)
        => $"失败 {count} 个：{details}";

    public static string PluginUpdateCompleted(string summary)
        => $"插件更新完成：{summary}。";
}
