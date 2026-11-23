using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Platforms;

/// <summary>
/// 按用户输入选择平台 Provider。默认内置 B 站 + Twitch。
/// V0.5-B 起注册表支持整体/单平台原子替换，供 PlatformPluginHost 在插件验签通过且版本更高时换表；
/// 消费者每次调用都是取当前快照，因此替换不需要重启。
/// </summary>
public sealed class PlatformFactory
{
    private IPlatformProvider[] _providers;

    public PlatformFactory(IEnumerable<IPlatformProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var array = providers.ToArray();
        if (array.Length == 0)
        {
            throw new ArgumentException("至少需要一个平台 Provider。", nameof(providers));
        }

        _providers = Deduplicate(array);
    }

    /// <summary>当前已注册平台的只读快照。</summary>
    public IReadOnlyList<IPlatformProvider> Providers => Volatile.Read(ref _providers);

    public bool CanHandle(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var providers = Volatile.Read(ref _providers);
        return providers.Any(provider => provider.CanHandle(roomUrl));
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
        var providers = Volatile.Read(ref _providers);
        var provider = providers.FirstOrDefault(candidate => candidate.Platform == platform);
        return provider ?? throw new NotSupportedException($"未注册平台 {platform} 的 Provider。");
    }

    public bool TryGetProvider(string roomUrl, out IPlatformProvider? provider)
    {
        provider = null;
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var providers = Volatile.Read(ref _providers);
        provider = providers.FirstOrDefault(candidate => candidate.CanHandle(roomUrl));
        return provider is not null;
    }

    /// <summary>
    /// 原子替换整个 Provider 注册表。同平台以「后出现的覆盖先出现的」去重（R9：注册表按平台覆盖，不能追加）。
    /// </summary>
    public void ReplaceProviders(IEnumerable<IPlatformProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var array = providers.ToArray();
        if (array.Length == 0)
        {
            throw new ArgumentException("至少需要一个平台 Provider。", nameof(providers));
        }

        Volatile.Write(ref _providers, Deduplicate(array));
    }

    /// <summary>原子替换单个平台 Provider：已注册则原位替换，未注册则追加。</summary>
    public void ReplaceProvider(IPlatformProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var current = Volatile.Read(ref _providers);
        var index = Array.FindIndex(current, candidate => candidate.Platform == provider.Platform);
        if (index < 0)
        {
            var appended = new IPlatformProvider[current.Length + 1];
            Array.Copy(current, appended, current.Length);
            appended[^1] = provider;
            Volatile.Write(ref _providers, appended);
            return;
        }

        var next = (IPlatformProvider[])current.Clone();
        next[index] = provider;
        Volatile.Write(ref _providers, next);
    }

    /// <summary>移除某平台 Provider；至少保留一个时才能移除（内置平台兜底不允许被清空）。</summary>
    public bool RemoveProvider(PlatformType platform)
    {
        var current = Volatile.Read(ref _providers);
        var next = current.Where(candidate => candidate.Platform != platform).ToArray();
        if (next.Length == current.Length)
        {
            return false;
        }

        if (next.Length == 0)
        {
            throw new InvalidOperationException("不能移除全部平台 Provider，至少保留一个内置平台。");
        }

        Volatile.Write(ref _providers, next);
        return true;
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

    private static IPlatformProvider[] Deduplicate(IReadOnlyList<IPlatformProvider> providers)
    {
        var ordered = new List<IPlatformProvider>(providers.Count);
        var indexByPlatform = new Dictionary<PlatformType, int>(providers.Count);
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            if (indexByPlatform.TryGetValue(provider.Platform, out var index))
            {
                ordered[index] = provider;
                continue;
            }

            indexByPlatform[provider.Platform] = ordered.Count;
            ordered.Add(provider);
        }

        return ordered.ToArray();
    }
}
