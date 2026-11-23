using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站内置平台插件入口（V0.5-B）。实现逻辑仍复用 <see cref="BilibiliProvider"/> 与
/// <see cref="BilibiliDanmakuClient"/>；这里只做 <see cref="IPlatformPlugin"/> 收敛，
/// 让内置平台与下载插件走同一条注册路径。
/// </summary>
public sealed class BilibiliPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.Bilibili;

    public string PluginId => "bilibili-native";

    public string Version => PluginVersionInfo.Resolve(typeof(BilibiliPlugin));

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new BilibiliProvider(httpClient);

    public IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null)
        => new BilibiliDanmakuClient(roomId, authCookie, httpClient);
}
