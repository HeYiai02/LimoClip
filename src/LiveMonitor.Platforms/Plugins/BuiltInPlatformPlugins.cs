using LiveMonitor.Core.Interfaces;
using LiveMonitor.Plugins.YouTube;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 内置平台插件入口（V0.5-B）。桌面端组合根只依赖 Platforms 项目，
/// 由这里集中 new 出 B 站/Twitch/YouTube 的 IPlatformPlugin 实现并登记版本，避免 Desktop 直接引用平台实现工程。
/// </summary>
public static class BuiltInPlatformPlugins
{
    /// <summary>创建全部内置平台插件实例（顺序：B 站、Twitch、YouTube）。</summary>
    public static IReadOnlyList<IPlatformPlugin> CreateAll() => new IPlatformPlugin[]
    {
        new BilibiliPlugin(),
        new TwitchPlugin(),
        new YouTubePlugin(),
    };
}
