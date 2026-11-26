using LiveMonitor.Danmaku;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

/// <summary>断线重连退避策略的纯逻辑单测（DanmakuManager 的重连循环只负责调用）。</summary>
public sealed class DanmakuReconnectPolicyTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 60)]
    [InlineData(100, 60)]
    [InlineData(-1, 3)]
    [InlineData(-100, 3)]
    public void DelayFor_MapsIndexToScheduleAndClamps(int index, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DanmakuReconnectPolicy.DelayFor(index));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(100, 3)]
    [InlineData(-5, 1)]
    public void NextAttemptIndex_AdvancesAndStopsAtLastSlot(int current, int expected)
    {
        Assert.Equal(expected, DanmakuReconnectPolicy.NextAttemptIndex(current));
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(2, 30, 0)]
    [InlineData(3, 31, 0)]
    [InlineData(2, 10, 2)]
    [InlineData(1, 29.999, 1)]
    [InlineData(3, 0, 3)]
    [InlineData(-2, 5, 0)]
    public void ResetIndexIfStable_OnlyResetsAfterStableThreshold(int current, double connectedSeconds, int expected)
    {
        var duration = TimeSpan.FromSeconds(connectedSeconds);
        Assert.Equal(expected, DanmakuReconnectPolicy.ResetIndexIfStable(current, duration));
    }

    [Fact]
    public void Schedule_ContractIsThreeTenThirtySixty()
    {
        Assert.Equal(4, DanmakuReconnectPolicy.BackoffSchedule.Length);
        Assert.Equal(TimeSpan.FromSeconds(3), DanmakuReconnectPolicy.BackoffSchedule[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), DanmakuReconnectPolicy.BackoffSchedule[1]);
        Assert.Equal(TimeSpan.FromSeconds(30), DanmakuReconnectPolicy.BackoffSchedule[2]);
        Assert.Equal(TimeSpan.FromSeconds(60), DanmakuReconnectPolicy.BackoffSchedule[3]);
        Assert.Equal(TimeSpan.FromSeconds(30), DanmakuReconnectPolicy.StableConnectionThreshold);
    }

    [Fact]
    public void SimulatedDisconnects_ProduceThreeTenThirtySixtySixtySequence()
    {
        var index = 0;
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            waits.Add(DanmakuReconnectPolicy.DelayFor(index));
            index = DanmakuReconnectPolicy.NextAttemptIndex(index);
        }

        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
            },
            waits);
    }

    [Fact]
    public void StableConnectionThenDisconnect_RestartsFromThreeSeconds()
    {
        var index = DanmakuReconnectPolicy.NextAttemptIndex(DanmakuReconnectPolicy.NextAttemptIndex(0));
        index = DanmakuReconnectPolicy.ResetIndexIfStable(index, TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(3), DanmakuReconnectPolicy.DelayFor(index));
    }
}
