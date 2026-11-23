using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class PlatformLoginProfilesTests
{
    [Fact]
    public void AllProfiles_CoverEverySupportedPlatform()
    {
        var platforms = PlatformLoginProfiles.All.Select(profile => profile.Platform).ToArray();

        Assert.Equal(6, platforms.Length);
        Assert.Contains(PlatformType.Bilibili, platforms);
        Assert.Contains(PlatformType.Twitch, platforms);
        Assert.Contains(PlatformType.YouTube, platforms);
        Assert.Contains(PlatformType.Huya, platforms);
        Assert.Contains(PlatformType.Douyu, platforms);
        Assert.Contains(PlatformType.Douyin, platforms);
        Assert.Equal(platforms.Length, platforms.Distinct().Count());
    }

    [Fact]
    public void EveryProfile_HasHttpsLoginPageAndCookieUris()
    {
        foreach (var profile in PlatformLoginProfiles.All)
        {
            Assert.StartsWith("https://", profile.LoginUrl, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(profile.CookieUris);
            Assert.All(profile.CookieUris, uri =>
                Assert.StartsWith("https://", uri, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
        }
    }

    [Fact]
    public void NamedCookiePlatforms_HaveCookieNamesAndAutoMarkers()
    {
        var bilibili = PlatformLoginProfiles.Get(PlatformType.Bilibili);
        Assert.Contains("SESSDATA", bilibili.CookieNames);
        Assert.Contains("SESSDATA", bilibili.AutoCaptureMarkers!);
        Assert.False(bilibili.CaptureAllCookies);

        var twitch = PlatformLoginProfiles.Get(PlatformType.Twitch);
        Assert.Contains("auth-token", twitch.CookieNames);
        Assert.Contains("auth-token", twitch.AutoCaptureMarkers!);

        var youtube = PlatformLoginProfiles.Get(PlatformType.YouTube);
        Assert.Contains("SAPISID", youtube.CookieNames);
        Assert.Contains("__Secure-3PSID", youtube.CookieNames);
        Assert.Contains("SAPISID", youtube.AutoCaptureMarkers!);
        // YouTube 的鉴权 Cookie 分布在 youtube.com（SID 等），需要至少一个该域 URI。
        Assert.Contains("https://www.youtube.com", youtube.CookieUris);
    }

    [Fact]
    public void CaptureAllPlatforms_UseManualCapture()
    {
        foreach (var platform in new[] { PlatformType.Huya, PlatformType.Douyu, PlatformType.Douyin })
        {
            var profile = PlatformLoginProfiles.Get(platform);
            Assert.True(profile.CaptureAllCookies, $"{platform} 应采集登录域下全部 Cookie");
            Assert.Empty(profile.CookieNames);
            // 没有自动标记 → 只能由用户在登录完成后点按钮采集，避免半登录状态误判。
            Assert.True(profile.AutoCaptureMarkers is null || profile.AutoCaptureMarkers.Count == 0);
        }
    }

    [Fact]
    public void TryGet_UnknownPlatform_ReturnsFalse()
    {
        Assert.False(PlatformLoginProfiles.TryGet((PlatformType)999, out var profile));
        Assert.Null(profile);
    }

    [Fact]
    public void DisplayName_UsesProfileTable()
    {
        Assert.Equal("B 站", WebViewLoginService.DisplayName(PlatformType.Bilibili));
        Assert.Equal("YouTube", WebViewLoginService.DisplayName(PlatformType.YouTube));
        Assert.Equal("抖音", WebViewLoginService.DisplayName(PlatformType.Douyin));
    }
}
