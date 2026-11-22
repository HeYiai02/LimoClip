using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Entities;

/// <summary>
/// 平台账号凭证。Cookie 由 Data 层用 Windows DPAPI 加密后再写入 EncryptedCookie。
/// </summary>
public sealed class PlatformCredential
{
    /// <summary>平台即主键，每个平台最多保存一份凭证。</summary>
    public PlatformType Platform { get; set; }

    public string? AccountName { get; set; }

    /// <summary>DPAPI 加密后的 Base64 文本，绝不能以明文落库。</summary>
    public string EncryptedCookie { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired { get; set; }
}
