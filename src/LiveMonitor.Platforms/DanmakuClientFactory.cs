using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// 默认弹幕客户端工厂：B 站与 Twitch 已支持实时弹幕，其余平台在 V0.2 暂不提供。
/// </summary>
public sealed class DanmakuClientFactory : IDanmakuClientFactory
{
    private readonly HttpClient _httpClient;

    public DanmakuClientFactory(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public IDanmakuClient? Create(PlatformType platform, string roomId, string? cookie)
    {
        ArgumentNullException.ThrowIfNull(roomId);

        return platform switch
        {
            PlatformType.Bilibili => new BilibiliDanmakuClient(roomId, cookie, _httpClient),
            PlatformType.Twitch => new TwitchDanmakuClient(roomId, cookie),
            _ => null
        };
    }
}
