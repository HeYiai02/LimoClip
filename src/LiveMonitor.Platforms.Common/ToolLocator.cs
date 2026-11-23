namespace LiveMonitor.Platforms;

/// <summary>
/// 外部工具与用户级目录定位（V0.5-B）。把“yt-dlp.exe 在哪”从各平台/工厂里收敛到一处，
/// 便于插件宿主与 CliUpdater 复用同一套搜索顺序。
/// </summary>
public static class ToolLocator
{
    public const string YtDlpFileName = "yt-dlp.exe";

    /// <summary>用户级插件根目录：%LocalAppData%/LiveMonitor/plugins。</summary>
    public static string PluginsRootDirectory => Path.Combine(AppDataRootDirectory, "plugins");

    /// <summary>用户级数据根目录：%LocalAppData%/LiveMonitor。</summary>
    public static string AppDataRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveMonitor");

    /// <summary>
    /// 按“显式配置 → 程序目录 → 程序目录/tools → 当前目录/tools → 当前目录”的顺序解析 yt-dlp.exe。
    /// 全部不存在时返回程序目录下的默认路径（保持旧行为：调用方在真正执行时才报缺失）。
    /// </summary>
    public static string ResolveYtDlpPath(string? configuredPath = null)
    {
        var candidates = BuildYtDlpCandidates(configuredPath);
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>仅返回实际存在的 yt-dlp.exe；不存在时返回 null（供更新检查等只读场景使用）。</summary>
    public static string? FindYtDlpPath(string? configuredPath = null)
    {
        return BuildYtDlpCandidates(configuredPath).FirstOrDefault(File.Exists);
    }

    private static string[] BuildYtDlpCandidates(string? configuredPath)
    {
        var list = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            list.Add(configuredPath.Trim());
        }

        var baseDirectory = AppContext.BaseDirectory;
        list.Add(Path.Combine(baseDirectory, YtDlpFileName));
        list.Add(Path.Combine(baseDirectory, "tools", YtDlpFileName));

        var currentDirectory = Environment.CurrentDirectory;
        list.Add(Path.Combine(currentDirectory, "tools", YtDlpFileName));
        list.Add(Path.Combine(currentDirectory, YtDlpFileName));

        return list.ToArray();
    }
}
