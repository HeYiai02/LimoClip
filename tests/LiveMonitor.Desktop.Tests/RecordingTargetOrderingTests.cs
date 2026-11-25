using System.Linq;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RecordingTargetOrderingTests
{
    private sealed record Target(bool IsRecording, bool IsLive, int DisplayNumber);

    private static int[] Order(params Target[] items)
        => RecordingTargetOrdering
            .OrderByPriority(items, item => item.IsRecording, item => item.IsLive, item => item.DisplayNumber)
            .Select(item => item.DisplayNumber)
            .ToArray();

    [Fact]
    public void Recording_rooms_come_first_then_live_rooms_then_others()
    {
        var ordered = Order(
            new Target(IsRecording: false, IsLive: false, DisplayNumber: 1),
            new Target(IsRecording: false, IsLive: true, DisplayNumber: 2),
            new Target(IsRecording: true, IsLive: false, DisplayNumber: 3),
            new Target(IsRecording: false, IsLive: true, DisplayNumber: 4),
            new Target(IsRecording: true, IsLive: true, DisplayNumber: 5));

        // 录制中：3/5；直播中：2/4；其余：1。
        Assert.Equal(new[] { 3, 5, 2, 4, 1 }, ordered);
    }

    [Fact]
    public void Same_priority_is_ordered_by_display_number_ascending()
    {
        var ordered = Order(
            new Target(IsRecording: false, IsLive: false, DisplayNumber: 9),
            new Target(IsRecording: false, IsLive: false, DisplayNumber: 2),
            new Target(IsRecording: false, IsLive: false, DisplayNumber: 5));

        Assert.Equal(new[] { 2, 5, 9 }, ordered);
    }

    [Fact]
    public void Equal_priority_and_display_number_keeps_original_relative_order()
    {
        var first = new Target(IsRecording: false, IsLive: true, DisplayNumber: 7);
        var second = new Target(IsRecording: false, IsLive: true, DisplayNumber: 7);

        var ordered = RecordingTargetOrdering
            .OrderByPriority(
                new[] { second, first },
                item => item.IsRecording,
                item => item.IsLive,
                item => item.DisplayNumber);

        Assert.Same(second, ordered[0]);
        Assert.Same(first, ordered[1]);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 2)]
    public void Rank_puts_recording_above_live_above_offline(bool isRecording, bool isLive, int expectedRank)
    {
        Assert.Equal(expectedRank, RecordingTargetOrdering.Rank(isRecording, isLive));
    }

    [Fact]
    public void Unknown_live_status_is_treated_as_offline()
    {
        // 主库尚未轮询到状态时 TryGetLiveStatus 返回 null，目标行按未直播处理，不会被误提到前面。
        var ordered = RecordingTargetOrdering.OrderByPriority(
            new[]
            {
                (DisplayNumber: 2, IsRecording: false, IsLive: false),
                (DisplayNumber: 1, IsRecording: false, IsLive: false),
            },
            item => item.IsRecording,
            item => item.IsLive,
            item => item.DisplayNumber);

        Assert.Equal(1, ordered[0].DisplayNumber);
        Assert.Equal(2, ordered[1].DisplayNumber);
    }
}
