using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>抖音平台插件入口。</summary>
public sealed class DouyinPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.Douyin;

    public string PluginId => "douyin";

    public string Version => PluginVersionInfo.Resolve(typeof(DouyinPlugin));

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new DouyinProvider(httpClient);

    public IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null)
        => new DouyinDanmakuClient(roomId, authCookie, httpClient);
}
