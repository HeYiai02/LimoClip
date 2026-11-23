using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 按平台创建弹幕客户端。接口放 Core，实现放 Platforms 聚合层，
/// 这样 LiveMonitor.Danmaku 只依赖 Core，不反向依赖具体平台工程。
/// </summary>
public interface IDanmakuClientFactory
{
    /// <summary>
    /// 创建指定平台/房间的弹幕客户端。尚未实现弹幕的返回 null（如抖音/斗鱼等）。
    /// </summary>
    /// <param name="platform">直播平台。</param>
    /// <param name="roomId">平台真实房间号/频道名。</param>
    /// <param name="cookie">完整 Cookie 头（由 ICredentialStore 解密）；未登录为 null。</param>
    IDanmakuClient? Create(PlatformType platform, string roomId, string? cookie);
}
