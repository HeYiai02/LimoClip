using System;
using System.Collections.Generic;
using System.Linq;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Services;

public sealed class PlatformLoginProfilesTests
{
    public static IEnumerable<object[]> AllProfiles =>
        PlatformLoginProfiles.All.Select(profile => new object[] { profile });

    [Fact]
    public void All_lists_six_supported_platforms_in_declared_order()
    {
        Assert.Equal(
            new[] { PlatformType.Bilibili, PlatformType.Twitch, PlatformType.YouTube, PlatformType.Huya, PlatformType.Douyu, PlatformType.Douyin },
            PlatformLoginProfiles.All.Select(profile => profile.Platform).ToArray());
    }

    [Fact]
    public void All_platforms_are_unique()
    {
        var platforms = PlatformLoginProfiles.All.Select(profile => profile.Platform).ToArray();
        Assert.Equal(platforms.Length, platforms.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Profile_has_non_blank_display_name(PlatformLoginProfile profile)
    {
        Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
        Assert.Equal(profile.DisplayName.Trim(), profile.DisplayName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Login_url_is_absolute_https(PlatformLoginProfile profile)
    {
        Assert.True(Uri.TryCreate(profile.LoginUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.False(string.IsNullOrWhiteSpace(uri.Host));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Cookie_uris_are_absolute_https(PlatformLoginProfile profile)
    {
        Assert.NotEmpty(profile.CookieUris);
        foreach (var cookieUri in profile.CookieUris)
        {
            Assert.True(Uri.TryCreate(cookieUri, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Cookie_names_are_non_empty_unless_capture_all(PlatformLoginProfile profile)
    {
        if (profile.CaptureAllCookies)
        {
            Assert.Empty(profile.CookieNames);
        }
        else
        {
            Assert.NotEmpty(profile.CookieNames);
        }

        foreach (var name in profile.CookieNames)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name.Trim(), name);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Auto_capture_markers_are_known_cookie_names(PlatformLoginProfile profile)
    {
        if (profile.AutoCaptureMarkers is null)
        {
            return;
        }

        Assert.NotEmpty(profile.AutoCaptureMarkers);
        var known = profile.CookieNames.ToHashSet(StringComparer.Ordinal);
        foreach (var marker in profile.AutoCaptureMarkers)
        {
            Assert.Contains(marker, known);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Supported_platforms_are_loggable_platforms(PlatformLoginProfile profile)
    {
        Assert.NotEqual(PlatformType.CustomStream, profile.Platform);
        Assert.DoesNotContain(profile.Platform, new[] { PlatformType.CustomStream });
    }

    [Fact]
    public void TryGet_known_platform_returns_same_instance()
    {
        Assert.True(PlatformLoginProfiles.TryGet(PlatformType.Bilibili, out var profile));
        Assert.Same(PlatformLoginProfiles.All.Single(item => item.Platform == PlatformType.Bilibili), profile);
        Assert.Equal("B 站", profile.DisplayName);
    }

    [Fact]
    public void TryGet_custom_stream_returns_false_and_null_profile()
    {
        Assert.False(PlatformLoginProfiles.TryGet(PlatformType.CustomStream, out var profile));
        Assert.Null(profile);
    }

    [Fact]
    public void Get_known_platform_matches_try_get()
    {
        foreach (var expected in PlatformLoginProfiles.All)
        {
            Assert.Equal(expected, PlatformLoginProfiles.Get(expected.Platform));
        }
    }

    [Fact]
    public void Get_unsupported_platform_throws_with_platform_name()
    {
        var error = Assert.Throws<NotSupportedException>(() => PlatformLoginProfiles.Get(PlatformType.CustomStream));
        Assert.Contains(PlatformType.CustomStream.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_all_platforms_still_declare_cookie_uris()
    {
        var captureAll = PlatformLoginProfiles.All.Where(profile => profile.CaptureAllCookies).ToArray();
        Assert.NotEmpty(captureAll);
        Assert.All(captureAll, profile => Assert.NotEmpty(profile.CookieUris));
    }
}
