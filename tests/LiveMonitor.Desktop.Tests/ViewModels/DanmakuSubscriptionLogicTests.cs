using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>弹幕订阅与开关规则（设置项解析、订阅资格、已有订阅保持条件）。</summary>
public sealed class DanmakuSubscriptionLogicTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("  FALSE  ", false)]
    public void IsEnabledSetting_defaults_to_enabled_unless_explicit_false(string? raw, bool expected)
    {
        Assert.Equal(expected, DanmakuSubscriptionLogic.IsEnabledSetting(raw));
    }

    [Theory]
    [InlineData(true, PlatformType.Bilibili, "123", true)]
    [InlineData(true, PlatformType.Douyu, "abc", true)]
    [InlineData(true, PlatformType.Twitch, "channel", true)]
    [InlineData(true, PlatformType.Douyin, "42", true)]
    [InlineData(true, PlatformType.Huya, "9", true)]
    [InlineData(true, PlatformType.CustomStream, "123", false)]
    [InlineData(false, PlatformType.Bilibili, "123", false)]
    [InlineData(true, PlatformType.Bilibili, "", false)]
    [InlineData(true, PlatformType.Bilibili, "   ", false)]
    [InlineData(true, PlatformType.Bilibili, null, false)]
    public void IsSubscribeEligible_filters_custom_stream_and_blank_rooms(
        bool hasRoom,
        PlatformType platform,
        string? roomId,
        bool expected)
    {
        Assert.Equal(expected, DanmakuSubscriptionLogic.IsSubscribeEligible(hasRoom, platform, roomId));
    }

    [Theory]
    [InlineData(true, PlatformType.Bilibili, "123", PlatformType.Bilibili, "123", true)]
    [InlineData(false, PlatformType.Bilibili, "123", PlatformType.Bilibili, "123", false)]
    [InlineData(true, PlatformType.Bilibili, "123", PlatformType.Douyu, "123", false)]
    [InlineData(true, PlatformType.Bilibili, "123", PlatformType.Bilibili, "456", false)]
    [InlineData(true, PlatformType.Bilibili, "123", PlatformType.Bilibili, null, false)]
    [InlineData(true, PlatformType.Bilibili, null, PlatformType.Bilibili, null, true)]
    public void ShouldKeepSubscription_requires_same_room_and_platform(
        bool stillActive,
        PlatformType platform,
        string? roomId,
        PlatformType existingPlatform,
        string? existingRoomId,
        bool expected)
    {
        Assert.Equal(
            expected,
            DanmakuSubscriptionLogic.ShouldKeepSubscription(
                stillActive,
                platform,
                roomId,
                existingPlatform,
                existingRoomId));
    }

    [Fact]
    public void ShouldKeepSubscription_room_id_comparison_is_ordinal()
    {
        Assert.True(DanmakuSubscriptionLogic.ShouldKeepSubscription(
            true, PlatformType.Bilibili, "Room1", PlatformType.Bilibili, "Room1"));
        Assert.False(DanmakuSubscriptionLogic.ShouldKeepSubscription(
            true, PlatformType.Bilibili, "Room1", PlatformType.Bilibili, "room1"));
    }
}
