using System.Security.Cryptography;
using System.Text;

namespace LiveMonitor.Data;

/// <summary>
/// Windows DPAPI 包装。Cookie 只在内存中为明文，落库前必须经过这里。
/// </summary>
public static class DpapiCookieProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveMonitor.V0.1.Cookie");

    public static string Protect(string cookie)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookie);

        var plain = Encoding.UTF8.GetBytes(cookie);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string protectedCookie)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedCookie);

        var encrypted = Convert.FromBase64String(protectedCookie);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool TryUnprotect(string protectedCookie, out string cookie)
    {
        cookie = string.Empty;
        if (string.IsNullOrWhiteSpace(protectedCookie))
        {
            return false;
        }

        try
        {
            cookie = Unprotect(protectedCookie);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
