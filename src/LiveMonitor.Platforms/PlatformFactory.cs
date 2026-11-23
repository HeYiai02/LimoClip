using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// 按用户输入选择平台 Provider。V0.1 固定注册 B 站 + Twitch。
/// </summary>
public sealed class PlatformFactory
{
    private readonly IReadOnlyList<IPlatformProvider> _providers;

    public PlatformFactory(IEnumerable<IPlatformProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        if (_providers.Count == 0)
        {
            throw new ArgumentException("至少需要一个平台 Provider。", nameof(providers));
        }
    }

    public IReadOnlyList<IPlatformProvider> Providers => _providers;

    public bool CanHandle(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        return _providers.Any(provider => provider.CanHandle(roomUrl));
    }

    public IPlatformProvider GetProvider(string roomUrl)
    {
        if (TryGetProvider(roomUrl, out var provider))
        {
            return provider!;
        }

        throw new NotSupportedException($"不支持的直播平台链接：{roomUrl}");
    }

    public IPlatformProvider GetProvider(PlatformType platform)
    {
        var provider = _providers.FirstOrDefault(candidate => candidate.Platform == platform);
        return provider ?? throw new NotSupportedException($"未注册平台 {platform} 的 Provider。");
    }

    public bool TryGetProvider(string roomUrl, out IPlatformProvider? provider)
    {
        provider = null;
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        provider = _providers.FirstOrDefault(candidate => candidate.CanHandle(roomUrl));
        return provider is not null;
    }

    public static PlatformFactory CreateDefault(HttpClient? httpClient = null, string? ytDlpPath = null)
    {
        var executable = ToolLocator.ResolveYtDlpPath(ytDlpPath);
        return new PlatformFactory(new IPlatformProvider[]
        {
            new BilibiliProvider(httpClient),
            new TwitchProvider(executable)
        });
    }
}
