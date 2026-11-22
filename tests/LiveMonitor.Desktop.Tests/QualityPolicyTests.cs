using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class QualityPolicyTests
{
    private static readonly IReadOnlyList<StreamQuality> Qualities =
    [
        Quality("1080", 1080, 60),
        Quality("720", 720, 30),
        Quality("480", 480, 30)
    ];

    private static StreamQuality Quality(string id, int height, int fps)
        => new(id, $"{height}P", height, fps, "https://example.test/live", new Dictionary<string, string>());

    private static RoomStatus Status() => new("room", PlatformType.Bilibili, "t", "a", true, Qualities);

    [Theory]
    [InlineData(800, 2, 1080)]
    [InlineData(600, 2, 720)]
    [InlineData(300, 2, 480)]
    [InlineData(800, 12, 480)]
    [InlineData(800, 11, 1080)]
    public void Choose_FollowsPaneHeightAndRoomCount(double height, int activeRooms, int expectedHeight)
    {
        var quality = QualityPolicy.Choose(Status(), height, activeRooms);

        Assert.NotNull(quality);
        Assert.Equal(expectedHeight, quality.Height);
    }

    [Fact]
    public void Choose_FocusSlotAlwaysUsesOriginalQuality()
    {
        var quality = QualityPolicy.Choose(Status(), 300, 20, isFocusSlot: true);

        Assert.NotNull(quality);
        Assert.Equal(1080, quality.Height);
    }

    [Fact]
    public void Choose_ReturnsNullWhenNoQualities()
    {
        var status = new RoomStatus("room", PlatformType.Bilibili, "t", "a", false, Array.Empty<StreamQuality>());

        Assert.Null(QualityPolicy.Choose(status, 800, 2));
    }

    [Theory]
    [InlineData(300, 1, true)]
    [InlineData(360, 1, false)]
    [InlineData(800, 12, true)]
    [InlineData(800, 11, false)]
    public void ShouldUseCompact_MatchesThresholds(double height, int activeRooms, bool expected)
    {
        Assert.Equal(expected, QualityPolicy.ShouldUseCompact(height, activeRooms));
    }
}
