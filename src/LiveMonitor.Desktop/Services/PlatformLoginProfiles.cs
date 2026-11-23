using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// WebView2 登录窗的平台配置：登录页地址、需要采集 Cookie 的 URI 与 Cookie 名称。
/// 新增平台时只在这里登记，登录窗与设置页会自动出现对应入口。
/// </summary>
public sealed record PlatformLoginProfile(
    PlatformType Platform,
    string DisplayName,
    string LoginUrl,
    IReadOnlyList<string> CookieUris,
    IReadOnlyList<string> CookieNames,
    bool CaptureAllCookies = false,
    IReadOnlyList<string>? AutoCaptureMarkers = null);

/// <summary>各平台 WebView2 登录配置表。</summary>
public static class PlatformLoginProfiles
{
    private static readonly PlatformLoginProfile[] Profiles =
    {
        new(
            PlatformType.Bilibili,
            "B 站",
            "https://passport.bilibili.com/login",
            new[] { "https://www.bilibili.com" },
            new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid" },
            AutoCaptureMarkers: new[] { "SESSDATA" }),
        new(
            PlatformType.Twitch,
            "Twitch",
            "https://www.twitch.tv/login",
            new[] { "https://www.twitch.tv" },
            new[] { "auth-token", "persistent", "unique_id", "twilight-user" },
            AutoCaptureMarkers: new[] { "auth-token" }),
        new(
            PlatformType.YouTube,
            "YouTube",
            // service=youtube 让登录完成后自动回到 YouTube，YouTube 域下会重新下发 SID/SAPISID 等鉴权 Cookie。
            "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F",
            new[] { "https://www.youtube.com", "https://accounts.google.com" },
            new[]
            {
                "SID", "HSID", "SSID", "APISID", "SAPISID", "LOGIN_INFO",
                "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PAPISID", "__Secure-3PAPISID",
            },
            AutoCaptureMarkers: new[] { "SAPISID", "__Secure-3PSID", "SID" }),
        new(
            PlatformType.Huya,
            "虎牙",
            "https://www.huya.com/",
            new[] { "https://www.huya.com" },
            Array.Empty<string>(),
            CaptureAllCookies: true),
        new(
            PlatformType.Douyu,
            "斗鱼",
            "https://www.douyu.com/",
            new[] { "https://www.douyu.com" },
            Array.Empty<string>(),
            CaptureAllCookies: true),
        new(
            PlatformType.Douyin,
            "抖音",
            "https://www.douyin.com/",
            new[] { "https://www.douyin.com", "https://live.douyin.com" },
            Array.Empty<string>(),
            CaptureAllCookies: true),
    };

    public static IReadOnlyList<PlatformLoginProfile> All => Profiles;

    public static bool TryGet(PlatformType platform, out PlatformLoginProfile profile)
    {
        foreach (var candidate in Profiles)
        {
            if (candidate.Platform == platform)
            {
                profile = candidate;
                return true;
            }
        }

        profile = null!;
        return false;
    }

    public static PlatformLoginProfile Get(PlatformType platform)
        => TryGet(platform, out var profile)
            ? profile
            : throw new NotSupportedException($"平台 {platform} 不支持 WebView2 登录。");
}
