using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// 默认弹幕客户端工厂。V0.5-B 起支持运行期注册/覆盖（插件在同平台声明弹幕后接管）；
/// 未注册的插件平台返回 null，<see cref="DanmakuManager"/> 以此表示“停止重试”。
/// </summary>
public sealed class DanmakuClientFactory : IDanmakuClientFactory
{
    private readonly HttpClient _httpClient;
    private volatile Dictionary<PlatformType, Func<string, string?, HttpClient?, IDanmakuClient?>> _overrides = new();

    public DanmakuClientFactory(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>当前是否有插件为该平台注册了弹幕实现。</summary>
    public bool HasOverride(PlatformType platform) => _overrides.ContainsKey(platform);

    /// <summary>注册或替换某平台的弹幕客户端构造器（插件热更新用；同平台后注册的覆盖先注册的）。</summary>
    public void RegisterOrReplace(
        PlatformType platform,
        Func<string, string?, HttpClient?, IDanmakuClient?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var current = _overrides;
        var next = new Dictionary<PlatformType, Func<string, string?, HttpClient?, IDanmakuClient?>>(current)
        {
            [platform] = factory,
        };
        _overrides = next;
    }

    /// <summary>移除某平台的插件弹幕注册（回退到内置实现）。</summary>
    public bool Remove(PlatformType platform)
    {
        var current = _overrides;
        if (!current.ContainsKey(platform))
        {
            return false;
        }

        var next = new Dictionary<PlatformType, Func<string, string?, HttpClient?, IDanmakuClient?>>(current);
        next.Remove(platform);
        _overrides = next;
        return true;
    }

    public IDanmakuClient? Create(PlatformType platform, string roomId, string? cookie)
    {
        ArgumentNullException.ThrowIfNull(roomId);

        var overrides = _overrides;
        if (overrides.TryGetValue(platform, out var factory))
        {
            return factory(roomId, cookie, _httpClient);
        }

        return platform switch
        {
            PlatformType.Bilibili => new BilibiliDanmakuClient(roomId, cookie, _httpClient),
            PlatformType.Twitch => new TwitchDanmakuClient(roomId, cookie),
            _ => null
        };
    }
}
