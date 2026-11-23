using System.Net.Http;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>斗鱼平台插件入口。</summary>
public sealed class DouyuPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.Douyu;

    public string PluginId => "douyu";

    public string Version => PluginVersionInfo.Resolve(typeof(DouyuPlugin));

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
    {
        _ = ytDlpPath; // 斗鱼不使用 yt-dlp
        return new DouyuProvider(httpClient);
    }

    public IDanmakuClient? CreateDanmakuClient(string roomId, string? authCookie = null, HttpClient? httpClient = null) =>
        new DouyuDanmakuClient(roomId, authCookie, httpClient);
}
