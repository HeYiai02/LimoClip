using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>
/// 剪辑台预览播放器位置推进纯逻辑（V0.7.5 抽离）的单元测试：
/// 边录边剪安全上界钳制、普通文件推进与 keep-open 完成收口、负值与安全区未建立的边界。
/// </summary>
public sealed class ClipPlaybackPositionLogicTests
{
    private static readonly TimeSpan Safe10 = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Duration60 = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void ClampSafeDuration_NegativeBecomesZero(int inputSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ClipPlaybackPositionLogic.ClampSafeDuration(TimeSpan.FromSeconds(inputSeconds)));
    }

    [Theory]
    [InlineData(15, 9.9)]
    [InlineData(9.9, 9.9)]
    [InlineData(5, 5)]
    [InlineData(-3, 0)]
    public void ClampLivePosition_WithSafeDuration_ClampsToMargin(double inputSeconds, double expectedSeconds)
    {
        var result = ClipPlaybackPositionLogic.ClampLivePosition(TimeSpan.FromSeconds(inputSeconds), Safe10);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void ClampLivePosition_WithoutSafeDuration_KeepsRawPosition()
    {
        var result = ClipPlaybackPositionLogic.ClampLivePosition(TimeSpan.FromSeconds(5), TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(5), result);
    }

    [Fact]
    public void ClampLivePosition_SafeDurationSmallerThanMargin_ClampsToZero()
    {
        var result = ClipPlaybackPositionLogic.ClampLivePosition(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50));
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void ClampPosition_NonLive_ClampsToDuration()
    {
        Assert.Equal(Duration60, ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(90), isLive: false, TimeSpan.Zero, Duration60));
        Assert.Equal(TimeSpan.FromSeconds(30), ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(30), isLive: false, TimeSpan.Zero, Duration60));
        Assert.Equal(TimeSpan.Zero, ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(-1), isLive: false, TimeSpan.Zero, Duration60));
    }

    [Fact]
    public void ClampPosition_NonLive_UnknownDuration_KeepsPositivePosition()
    {
        var result = ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(90), isLive: false, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(90), result);
    }

    [Fact]
    public void ClampPosition_Live_WithSafeDuration_UsesLiveBound()
    {
        var result = ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(15), isLive: true, Safe10, Duration60);
        Assert.Equal(TimeSpan.FromSeconds(9.9), result);
    }

    [Fact]
    public void ClampPosition_Live_WithoutSafeDuration_FallsBackToDuration()
    {
        // 与重构前一致：安全区尚未建立时走普通时长钳制，而不是放开到任意位置。
        var result = ClipPlaybackPositionLogic.ClampPosition(TimeSpan.FromSeconds(90), isLive: true, TimeSpan.Zero, Duration60);
        Assert.Equal(Duration60, result);
    }

    [Fact]
    public void Evaluate_Live_ClampsAndAdvances()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5),
            isLive: true,
            Safe10,
            Duration60,
            paused: false);

        Assert.Equal(TimeSpan.FromSeconds(9.9), result.Position);
        Assert.True(result.PositionChanged);
        Assert.False(result.Completed);
    }

    [Fact]
    public void Evaluate_Live_WithoutSafeDuration_AdvancesRawPosition()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            isLive: true,
            TimeSpan.Zero,
            TimeSpan.Zero,
            paused: false);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Position);
        Assert.True(result.PositionChanged);
        Assert.False(result.Completed);
    }

    [Fact]
    public void Evaluate_Live_NearEof_DoesNotComplete()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(100),
            TimeSpan.FromSeconds(9),
            isLive: true,
            Safe10,
            Duration60,
            paused: false);

        Assert.Equal(TimeSpan.FromSeconds(9.9), result.Position);
        Assert.True(result.PositionChanged);
        Assert.False(result.Completed);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.9)]
    public void Evaluate_WithinEpsilon_NoChange(double deltaMilliseconds)
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromMilliseconds(1000 + deltaMilliseconds),
            TimeSpan.FromMilliseconds(1000),
            isLive: false,
            TimeSpan.Zero,
            Duration60,
            paused: false);

        Assert.False(result.PositionChanged);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), result.Position);
    }

    [Fact]
    public void Evaluate_AdvancesNormalPlayback()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2.9),
            isLive: false,
            TimeSpan.Zero,
            Duration60,
            paused: false);

        Assert.Equal(TimeSpan.FromSeconds(3), result.Position);
        Assert.True(result.PositionChanged);
        Assert.False(result.Completed);
    }

    [Fact]
    public void Evaluate_NearEnd_CompletesWithDuration()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(59.95),
            TimeSpan.FromSeconds(59.9),
            isLive: false,
            TimeSpan.Zero,
            Duration60,
            paused: false);

        Assert.Equal(Duration60, result.Position);
        Assert.True(result.PositionChanged);
        Assert.True(result.Completed);
    }

    [Fact]
    public void Evaluate_PausedNearEnd_DoesNotComplete()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(59.99),
            TimeSpan.FromSeconds(59.9),
            isLive: false,
            TimeSpan.Zero,
            Duration60,
            paused: true);

        Assert.Equal(TimeSpan.FromSeconds(59.99), result.Position);
        Assert.True(result.PositionChanged);
        Assert.False(result.Completed);
    }

    [Fact]
    public void Evaluate_UnknownDuration_DoesNotComplete()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(59.99),
            TimeSpan.FromSeconds(59.9),
            isLive: false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            paused: false);

        Assert.False(result.Completed);
    }

    [Fact]
    public void Evaluate_BeforeEndTolerance_DoesNotComplete()
    {
        var result = ClipPlaybackPositionLogic.Evaluate(
            TimeSpan.FromSeconds(59),
            TimeSpan.FromSeconds(58.9),
            isLive: false,
            TimeSpan.Zero,
            Duration60,
            paused: false);

        Assert.Equal(TimeSpan.FromSeconds(59), result.Position);
        Assert.False(result.Completed);
    }
}
