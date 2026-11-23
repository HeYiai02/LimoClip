using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class DanmakuLaneAllocatorTests
{
    private const double Width = 600;
    private const double SpawnGap = 24;

    [Fact]
    public void EmptyOverlay_AllocatesTopLane()
    {
        Assert.Equal(0, FindFreeLane(10));
    }

    [Fact]
    public void LaneWithEnteringItem_IsSkipped()
    {
        // 轨道 0/1 的最后一条弹幕还在右侧入场区（右边缘 > width - gap），只能落到下一轨。
        var lane = FindFreeLane(10, (0, Width + 4), (1, Width + 40));

        Assert.Equal(2, lane);
    }

    [Fact]
    public void LowerLaneOccupied_StillAllocatesTopLane()
    {
        // 回归用例：旧实现会从 _nextLane 轮转，稀疏弹幕只占连续几条轨道，
        // 带子转到下半屏时上半屏为空。现在必须优先使用上方空闲轨道。
        var lane = FindFreeLane(10, (7, 120));

        Assert.Equal(0, lane);
    }

    [Fact]
    public void TopLanesBusy_AllocatesFirstFreeLane()
    {
        var lane = FindFreeLane(
            10,
            (0, Width + 4),
            (1, Width + 40),
            (2, Width + 80),
            (3, 100));

        Assert.Equal(3, lane);
    }

    [Fact]
    public void FreedLane_IsReusedFromTop()
    {
        // 轨道 0 的弹幕已经完全进场（右边缘远小于 width - gap），新弹幕应回到顶部。
        var lane = FindFreeLane(10, (0, 100), (1, Width + 4), (2, Width + 4));

        Assert.Equal(0, lane);
    }

    [Fact]
    public void AllLanesBusy_ReturnsMinusOne()
    {
        var occupied = Enumerable.Range(0, 10).Select(lane => (lane, Width + 4)).ToArray();

        Assert.Equal(-1, FindFreeLane(10, occupied));
    }

    [Fact]
    public void InvalidSize_ReturnsMinusOne()
    {
        Assert.Equal(-1, DanmakuLaneAllocator.FindFreeLane(0, _ => double.NegativeInfinity, Width, SpawnGap));
        Assert.Equal(-1, DanmakuLaneAllocator.FindFreeLane(10, _ => double.NegativeInfinity, 0, SpawnGap));
    }

    [Fact]
    public void NullAccessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DanmakuLaneAllocator.FindFreeLane(10, null!, Width, SpawnGap));
    }

    private static int FindFreeLane(int laneCount, params (int Lane, double RightEdge)[] items)
    {
        return DanmakuLaneAllocator.FindFreeLane(
            laneCount,
            lane =>
            {
                foreach (var item in items)
                {
                    if (item.Lane == lane)
                    {
                        return item.RightEdge;
                    }
                }

                return double.NegativeInfinity;
            },
            Width,
            SpawnGap);
    }
}
