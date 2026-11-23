namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 平台插件信任根与更新源配置。
/// 公钥对应离线保存的 Ed25519 私钥（仅发布方持有，不进入仓库）；插件 DLL 验签失败一律拒载。
/// </summary>
public static class PluginTrust
{
    /// <summary>插件签名信任根（Ed25519 公钥，32 字节 Base64）。</summary>
    public const string PublicKeyBase64 = "lYn7fkrnrtAHz4fplCQ3R5yC3pPCrG3QLk5atHiSE24=";

    /// <summary>providers-manifest.json 主地址（GitHub Raw）。</summary>
    public const string ManifestPrimaryUrl =
        "https://raw.githubusercontent.com/heyiai02/LimoClip/main/plugins/providers-manifest.json";

    /// <summary>providers-manifest.json 镜像地址（国内网络不通 GitHub 时使用，留空表示未配置）。</summary>
    public const string ManifestMirrorUrl = "";

    /// <summary>插件本机落地根目录。</summary>
    public static string PluginsDirectory => LiveMonitor.Platforms.ToolLocator.PluginsRootDirectory;

    /// <summary>返回按优先级排列的 manifest 地址（主地址 + 已配置的镜像）。</summary>
    public static IReadOnlyList<string> ManifestUrls()
    {
        var urls = new List<string> { ManifestPrimaryUrl };
        if (!string.IsNullOrWhiteSpace(ManifestMirrorUrl))
        {
            urls.Add(ManifestMirrorUrl);
        }

        return urls;
    }
}
