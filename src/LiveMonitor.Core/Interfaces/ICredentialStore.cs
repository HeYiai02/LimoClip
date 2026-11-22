using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 平台 Cookie 的安全存取。实现放在 Data 层，内部使用 DPAPI，对外只暴露明文 Cookie。
/// </summary>
public interface ICredentialStore
{
    /// <summary>返回解密后的 Cookie；未保存、已过期或解密失败时返回 null。</summary>
    Task<string?> GetCookieAsync(PlatformType platform, CancellationToken ct = default);

    /// <summary>加密并保存指定平台 Cookie，同时清除过期标记。</summary>
    Task SaveAsync(
        PlatformType platform,
        string? accountName,
        string cookie,
        CancellationToken ct = default);

    Task RemoveAsync(PlatformType platform, CancellationToken ct = default);

    Task MarkExpiredAsync(PlatformType platform, CancellationToken ct = default);

    Task<bool> IsExpiredAsync(PlatformType platform, CancellationToken ct = default);
}
