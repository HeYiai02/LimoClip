using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 平台插件统一入口契约（V0.5-B）。
/// 所有平台（内置的 B 站/Twitch 与后续 YouTube/虎牙/斗鱼/抖音）都把
/// <see cref="IPlatformProvider"/> 与 <see cref="IDanmakuClient"/> 的创建逻辑收敛到这里，
/// 由 <c>PlatformPluginHost</c> 通过 <c>AssemblyLoadContext</c> 动态加载并注册进两个工厂。
/// </summary>
public interface IPlatformPlugin
{
    /// <summary>插件对应的平台枚举。<see cref="PlatformType"/> 的数值已入库，插件不得自定义新值。</summary>
    PlatformType Platform { get; }

    /// <summary>稳定插件标识，如 "bilibili"、"twitch"、"youtube"、"huya"、"douyu"、"douyin"。</summary>
    string PluginId { get; }

    /// <summary>SemVer 版本号（如 "1.0.0"），用于与 <c>providers-manifest.json</c> 比较是否需要更新。</summary>
    string Version { get; }

    /// <summary>
    /// 创建拉流/状态查询 Provider。
    /// 插件不得自行创建 HttpClient，统一使用宿主传入的实例（带 UA、超时与连接复用）。
    /// </summary>
    /// <param name="httpClient">宿主共享的 HttpClient（20 秒超时，UA 为 <c>PlatformDefaults.UserAgent</c>）。</param>
    /// <param name="ytDlpPath">宿主解析好的 yt-dlp.exe 路径；不使用 yt-dlp 的插件可忽略。</param>
    IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath);

    /// <summary>
    /// 创建弹幕客户端。返回 null 表示该平台不支持实时弹幕：
    /// <c>DanmakuManager</c> 以 null 表示“停止重试”，不得用抛异常代替（见 DanmakuManager 对应分支）。
    /// </summary>
    /// <param name="roomId">平台侧房间标识（已由 Provider 解析为平台真实房间号/登录名）。</param>
    /// <param name="authCookie">完整 Cookie 头，可为空（匿名连接）。</param>
    /// <param name="httpClient">宿主共享 HttpClient，可为空时插件自行降级处理。</param>
    IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null);
}
