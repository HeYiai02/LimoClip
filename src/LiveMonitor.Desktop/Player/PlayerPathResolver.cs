namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 播放器路径解析规则：CA 证书查找顺序与音量表临时目录选择。
/// 只做候选路径计算，不创建目录、不读环境变量，便于直接单测。
/// </summary>
internal static class PlayerPathResolver
{
    /// <summary>
    /// CA 证书查找顺序：显式配置（存在时）→ 程序目录 cacert.crt → 程序目录 cacert.pem。
    /// </summary>
    public static string? ResolveCaFile(string? configuredPath, string baseDirectory, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && fileExists(configuredPath!))
        {
            return configuredPath;
        }

        var bundledCrt = Path.Combine(baseDirectory, "cacert.crt");
        if (fileExists(bundledCrt))
        {
            return bundledCrt;
        }

        var bundledPem = Path.Combine(baseDirectory, "cacert.pem");
        return fileExists(bundledPem) ? bundledPem : null;
    }

    /// <summary>
    /// 音量表输出目录：优先 `%LocalAppData%/LiveMonitor/audio-meter`；
    /// 当 LocalAppData 不可用（为空或退化为相对路径）时回退到程序目录下的 audio-meter。
    /// </summary>
    public static string ResolveAudioMeterRoot(string? localAppData, string baseDirectory)
    {
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? string.Empty
            : Path.Combine(localAppData!, "LiveMonitor", "audio-meter");

        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("LiveMonitor", StringComparison.Ordinal))
        {
            root = Path.Combine(baseDirectory, "audio-meter");
        }

        return root;
    }
}
