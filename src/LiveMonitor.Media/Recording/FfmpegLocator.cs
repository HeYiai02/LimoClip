namespace LiveMonitor.Media.Recording;

/// <summary>
/// 定位 ffmpeg 可执行文件。查找顺序：
/// ① 程序目录下 <c>tools\ffmpeg\ffmpeg.exe</c>（发布后布局）；
/// ② 当前工作目录下 <c>tools\ffmpeg\ffmpeg.exe</c>；
/// ③ 从程序目录向上最多 8 层查找仓库根的 <c>tools\ffmpeg\ffmpeg.exe</c>（开发/测试 bin 目录布局）。
/// </summary>
public static class FfmpegLocator
{
    public const string RelativePath = @"tools\ffmpeg\ffmpeg.exe";

    /// <summary>随包 CA 证书：静态链接 ffmpeg 注册 Windows 证书库失败时用 <c>-ca_file</c> 兜底。</summary>
    public const string CaBundleRelativePath = @"tools\cacert.crt";

    private const int MaxWalkUpLevels = 8;

    /// <summary>尝试定位 ffmpeg；找不到返回 false。</summary>
    public static bool TryLocate(out string ffmpegPath)
    {
        return TryLocateRelative(RelativePath, out ffmpegPath);
    }

    /// <summary>定位 ffmpeg，找不到抛出带搜索路径说明的 <see cref="FileNotFoundException"/>。</summary>
    public static string Locate()
    {
        return LocateRelative(RelativePath);
    }

    /// <summary>
    /// 尝试定位随包 CA 证书（<c>cacert.crt</c>）。发布布局在程序目录根（与 exe 同级），
    /// 开发布局在仓库 <c>tools\cacert.crt</c>；两者都会按层级向上查找。
    /// </summary>
    public static bool TryLocateCaBundle(out string caBundlePath)
    {
        foreach (var candidate in EnumerateCaBundleCandidates())
        {
            if (File.Exists(candidate))
            {
                caBundlePath = Path.GetFullPath(candidate);
                return true;
            }
        }

        caBundlePath = string.Empty;
        return false;
    }

    /// <summary>定位 CA 证书；找不到返回 null（调用方可不传 <c>-ca_file</c>）。</summary>
    public static string? LocateCaBundle()
    {
        return TryLocateCaBundle(out var path) ? path : null;
    }

    private static bool TryLocateRelative(string relativePath, out string locatedPath)
    {
        foreach (var candidate in EnumerateCandidates(relativePath))
        {
            if (File.Exists(candidate))
            {
                locatedPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        locatedPath = string.Empty;
        return false;
    }

    private static string LocateRelative(string relativePath)
    {
        if (TryLocateRelative(relativePath, out var path))
        {
            return path;
        }

        var searched = string.Join(
            Environment.NewLine,
            EnumerateCandidates(relativePath).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));

        throw new FileNotFoundException(
            $"找不到 ffmpeg 可执行文件，已搜索：{Environment.NewLine}{searched}",
            relativePath);
    }

    private static IEnumerable<string> EnumerateCandidates(string relativePath)
    {
        yield return Path.Combine(AppContext.BaseDirectory, relativePath);
        yield return Path.Combine(Environment.CurrentDirectory, relativePath);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < MaxWalkUpLevels && directory is not null; level++)
        {
            yield return Path.Combine(directory.FullName, relativePath);
            directory = directory.Parent;
        }
    }

    private static IEnumerable<string> EnumerateCaBundleCandidates()
    {
        // 发布布局：cacert.crt 被复制到程序目录根（见 Desktop.csproj Link="cacert.crt"）。
        yield return Path.Combine(AppContext.BaseDirectory, "cacert.crt");
        yield return Path.Combine(Environment.CurrentDirectory, "cacert.crt");

        // 开发/测试布局：仓库 tools\cacert.crt，从 bin 目录向上找。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < MaxWalkUpLevels && directory is not null; level++)
        {
            yield return Path.Combine(directory.FullName, CaBundleRelativePath);
            directory = directory.Parent;
        }
    }
}
