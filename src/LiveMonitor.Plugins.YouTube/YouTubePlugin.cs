using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.YouTube;

/// <summary>
/// YouTube 平台插件入口：向主程序提供流状态解析（yt-dlp）与实时弹幕（InnerTube 聊天轮询）。
/// </summary>
public sealed class YouTubePlugin : IPlatformPlugin
{
    /// <inheritdoc />
    public PlatformType Platform => PlatformType.YouTube;

    /// <inheritdoc />
    public string PluginId => "youtube";

    /// <inheritdoc />
    public string Version => PluginVersionInfo.Resolve(typeof(YouTubePlugin));

    /// <inheritdoc />
    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new YouTubeProvider(ytDlpPath);

    /// <inheritdoc />
    public IDanmakuClient? CreateDanmakuClient(string roomId, string? authCookie = null, HttpClient? httpClient = null)
        => new YouTubeDanmakuClient(roomId, authCookie, httpClient);
}
