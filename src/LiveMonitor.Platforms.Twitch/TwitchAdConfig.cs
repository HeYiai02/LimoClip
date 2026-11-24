namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 去广告（方案 B）的运行时配置。
/// <see cref="PlaylistProxyTemplateProvider"/> 由桌面端在启动时读取 AppSettings 注入，
/// 返回形如 <c>https://eu.luminous.dev/live/{channel}</c> 的模板；返回 null 表示不使用代理。
/// 这里刻意用静态委托，避免让 Provider 依赖桌面端的设置仓储，也便于插件签名/动态加载。
/// </summary>
public static class TwitchAdConfig
{
    /// <summary>
    /// 外部去广告 M3U8 代理模板；<c>{channel}</c> 会被替换成频道名（URL 编码后的小写频道）。
    /// 返回 null/空表示跳过代理层，直接走原生 GQL + Usher。
    /// </summary>
    public static Func<string?>? PlaylistProxyTemplateProvider { get; set; }
}
