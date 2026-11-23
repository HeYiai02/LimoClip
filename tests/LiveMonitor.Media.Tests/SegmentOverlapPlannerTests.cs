using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>分段录制合并时的重叠裁剪计算。</summary>
public sealed class SegmentOverlapPlannerTests
{
    private static RecordedSegment Segment(double durationSeconds, double? startSeconds, string name)
        => new(name, TimeSpan.FromSeconds(durationSeconds), startSeconds);

    [Fact]
    public void ComputeStartTrims_OverlappingTimeline_TrimsDuplicatePrefix()
    {
        // 虎牙实测：第二段从 1.1 秒前重新下发，与第一段尾部重叠。
        var segments = new[]
        {
            Segment(2.0, 100.0, "part1"),
            Segment(3.0, 100.9, "part2"),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        Assert.Equal(0, trims[0].TotalSeconds, 3);
        Assert.Equal(1.1, trims[1].TotalSeconds, 3);
        Assert.Equal(3.9, SegmentOverlapPlanner.ComputeContentDuration(segments).TotalSeconds, 3);
    }

    [Fact]
    public void ComputeStartTrims_ContiguousTimeline_DoesNotTrim()
    {
        var segments = new[]
        {
            Segment(2.0, 100.0, "part1"),
            Segment(3.0, 102.5, "part2"),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        Assert.Equal(0, trims[1].TotalSeconds, 3);
        Assert.Equal(5.0, SegmentOverlapPlanner.ComputeContentDuration(segments).TotalSeconds, 3);
    }

    [Fact]
    public void ComputeStartTrims_TimelineRestart_DoesNotTrim()
    {
        // 流自身时间轴从 0 重新计时时不能按服务器时间轴裁剪，否则会把正常内容裁掉。
        var segments = new[]
        {
            Segment(2.0, 100.0, "part1"),
            Segment(3.0, 0.2, "part2"),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        Assert.Equal(0, trims[1].TotalSeconds, 3);
    }

    [Fact]
    public void ComputeStartTrims_MissingTimeline_KeepsSegmentWhole()
    {
        var segments = new[]
        {
            Segment(2.0, null, "part1"),
            Segment(3.0, 101.0, "part2"),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        Assert.All(trims, trim => Assert.Equal(0, trim.TotalSeconds, 3));
    }

    [Fact]
    public void ComputeStartTrims_OverlapLargerThanDuration_ClampsToKeepMinimum()
    {
        var segments = new[]
        {
            Segment(2.0, 100.0, "part1"),
            Segment(1.0, 95.0, "part2"),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        // 至少保留 0.2 秒：1.0 - 0.2 = 0.8 秒可裁。
        Assert.Equal(0.8, trims[1].TotalSeconds, 3);
    }

    [Fact]
    public void ComputeStartTrims_WallClockOffset_TakesPrecedenceOverSourceTimeline()
    {
        // 第二段的输入时间轴起点异常（0，会被当成时间轴重启），但墙钟偏移表明它在第 2 秒开始，
        // 第一段内容 6 秒，因此应裁掉 4 秒重叠。
        var segments = new[]
        {
            new RecordedSegment("part1", TimeSpan.FromSeconds(6), 1234.0, TimeSpan.Zero),
            new RecordedSegment("part2", TimeSpan.FromSeconds(5), 0.0, TimeSpan.FromSeconds(2)),
        };

        var trims = SegmentOverlapPlanner.ComputeStartTrims(segments);

        Assert.Equal(4.0, trims[1].TotalSeconds, 3);
        Assert.Equal(7.0, SegmentOverlapPlanner.ComputeContentDuration(segments).TotalSeconds, 3);
    }
}
