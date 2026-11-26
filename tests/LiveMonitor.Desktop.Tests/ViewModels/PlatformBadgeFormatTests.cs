using System;
using System.Collections.Generic;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>平台徽章与名称唯一来源（`PlatformBadgeFormat`）的直接单测。</summary>
public sealed class PlatformBadgeFormatTests
{
    public static IEnumerable<object[]> KnownPlatforms()
    {
        yield return new object[] { PlatformType.Bilibili, "B站", "#FB7299", "#FFFFFF", "哔哩哔哩" };
        yield return new object[] { PlatformType.Douyin, "抖音", "#FE2C55", "#FFFFFF", "抖音" };
        yield return new object[] { PlatformType.Douyu, "斗鱼", "#FF6A00", "#FFFFFF", "斗鱼" };
        yield return new object[] { PlatformType.Huya, "虎牙", "#FFB300", "#1A1A1A", "虎牙" };
        yield return new object[] { PlatformType.Twitch, "Twitch", "#9146FF", "#FFFFFF", "Twitch" };
        yield return new object[] { PlatformType.YouTube, "YouTube", "#FF0033", "#FFFFFF", "YouTube" };
        yield return new object[] { PlatformType.CustomStream, "清流", "#0EA5E9", "#FFFFFF", "自定义清流" };
    }

    [Theory]
    [MemberData(nameof(KnownPlatforms))]
    public void Text_Background_Foreground_And_DisplayName_Per_Platform(
        PlatformType platform, string text, string background, string foreground, string displayName)
    {
        Assert.Equal(text, PlatformBadgeFormat.Text(platform));
        Assert.Equal(background, PlatformBadgeFormat.Background(platform));
        Assert.Equal(foreground, PlatformBadgeFormat.Foreground(platform));
        Assert.Equal(displayName, PlatformBadgeFormat.DisplayName(platform));
    }

    [Fact]
    public void UnknownPlatform_FallsBackToDash_DefaultBrushes_And_OtherDisplayName()
    {
        var unknown = (PlatformType)42;
        Assert.Equal("—", PlatformBadgeFormat.Text(unknown));
        Assert.Equal("#2A3140", PlatformBadgeFormat.Background(unknown));
        Assert.Equal("#FFFFFF", PlatformBadgeFormat.Foreground(unknown));
        Assert.Equal("其他平台", PlatformBadgeFormat.DisplayName(unknown));
    }

    [Fact]
    public void OnlyHuya_UsesDarkForeground()
    {
        foreach (var platform in Enum.GetValues<PlatformType>())
        {
            Assert.Equal(platform == PlatformType.Huya ? "#1A1A1A" : "#FFFFFF", PlatformBadgeFormat.Foreground(platform));
        }
    }

    [Fact]
    public void ShortBadges_AreDistinctPerPlatform()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var platform in Enum.GetValues<PlatformType>())
        {
            Assert.True(seen.Add(PlatformBadgeFormat.Text(platform)), $"重复徽章文字：{platform}");
        }
    }

    [Fact]
    public void CustomStream_DisplayName_is_explicit_not_abbreviated()
    {
        Assert.Equal("清流", PlatformBadgeFormat.Text(PlatformType.CustomStream));
        Assert.Equal("自定义清流", PlatformBadgeFormat.DisplayName(PlatformType.CustomStream));
    }
}
