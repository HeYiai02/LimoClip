using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 内置平台插件入口（V0.5-B）。Twitch 拉流依赖 yt-dlp，弹幕为匿名 IRC 只读。
/// </summary>
public sealed class TwitchPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.Twitch;

    public string PluginId => "twitch-native";

    public string Version => PluginVersionInfo.Resolve(typeof(TwitchPlugin));

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new TwitchProvider(
            ytDlpPath,
            httpClient,
            () => TwitchAdConfig.PlaylistProxyTemplateProvider?.Invoke());

    public IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null)
        => new TwitchDanmakuClient(roomId, authCookie);
}
