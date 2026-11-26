using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>
/// 回放进度条纯逻辑（V0.7.5 抽离）的单元测试：可回放判定、滑块增量、拖动防抖目标、
/// 时间文本格式。对应 v5 清单 4.2 中「可回放 seek 需补最小测试」的决策点。
/// </summary>
public sealed class ReplaySeekLogicTests
{
    [Fact]
    public void IsReplayable_SeekableYesWithFiniteDuration_ReturnsTotal()
    {
        Assert.True(ReplaySeekLogic.IsReplayable("YES", 120.5, out var total));
        Assert.Equal(120.5, total);
    }

    [Theory]
    [InlineData(null, 120d)]
    [InlineData("no", 120d)]
    [InlineData("yes", null)]
    [InlineData("yes", double.NaN)]
    [InlineData("yes", double.PositiveInfinity)]
    [InlineData("yes", 0.5d)]
    public void IsReplayable_InvalidProbe_ReturnsFalse(string? seekable, double? duration)
    {
        Assert.False(ReplaySeekLogic.IsReplayable(seekable, duration, out var total));
        Assert.Equal(0d, total);
    }

    [Fact]
    public void Update_ValidProbe_ShowsBarAndSetsMaximum()
    {
        var logic = new ReplaySeekLogic();

        var update = logic.Update("yes", 120d, 0d, 0d, 0d);

        Assert.True(update.IsVisible);
        Assert.True(update.VisibilityChanged);
        Assert.Equal(120d, update.Maximum);
        Assert.Null(update.Value);
        Assert.Equal("00:00 / 02:00", update.TimeText);
        Assert.True(logic.IsAvailable);
        Assert.Equal(120d, logic.Duration);
    }

    [Fact]
    public void Update_NotReplayableAfterVisible_HidesAndResets()
    {
        var logic = new ReplaySeekLogic();
        logic.Update("yes", 120d, 0d, 120d, 0d);

        var update = logic.Update("no", null, null, 120d, 0d);

        Assert.False(update.IsVisible);
        Assert.True(update.VisibilityChanged);
        Assert.Null(update.Maximum);
        Assert.Null(update.Value);
        Assert.Null(update.TimeText);
        Assert.False(logic.IsAvailable);
        Assert.Equal(0d, logic.Duration);
    }

    [Fact]
    public void Update_SameMaximumWithinTolerance_OmitsMaximumWrite()
    {
        var logic = new ReplaySeekLogic();

        var update = logic.Update("yes", 120d, 60.2d, 120d, 60d);

        Assert.Null(update.Maximum);
        Assert.Null(update.Value);
        Assert.Equal("01:00 / 02:00", update.TimeText);
    }

    [Fact]
    public void Update_PositionBeyondDuration_ClampsToDuration()
    {
        var logic = new ReplaySeekLogic();

        var update = logic.Update("yes", 120d, 500d, 120d, 0d);

        Assert.Equal(120d, update.Value);
        Assert.Equal("02:00 / 02:00", update.TimeText);
    }

    [Fact]
    public void Update_PositionBeforeZero_ClampsToZero()
    {
        var logic = new ReplaySeekLogic();

        var update = logic.Update("yes", 120d, -5d, 120d, 30d);

        Assert.Equal(0d, update.Value);
        Assert.Equal("00:00 / 02:00", update.TimeText);
    }

    [Fact]
    public void Update_WhileDragging_DoesNotOverwriteSliderValue()
    {
        var logic = new ReplaySeekLogic();
        logic.Update("yes", 120d, 0d, 120d, 0d);
        logic.BeginDrag(30d);

        var update = logic.Update("yes", 120d, 90d, 120d, 30d);

        Assert.Null(update.Value);
        Assert.Equal("00:30 / 02:00", update.TimeText);
    }

    [Fact]
    public void BeginDrag_FormatsInvariantTargetAndClearsAfterTake()
    {
        var logic = new ReplaySeekLogic();
        logic.BeginDrag(12.3456d);

        Assert.True(logic.TryTakePendingTarget(out var target));
        Assert.Equal("12.346", target);
        Assert.False(logic.TryTakePendingTarget(out _));
        Assert.False(logic.IsDragging);
    }

    [Theory]
    [InlineData(-5d, "0")]
    [InlineData(double.NaN, "0")]
    [InlineData(0d, "0")]
    public void BeginDrag_InvalidValue_CoercedToZero(double seconds, string expected)
    {
        var logic = new ReplaySeekLogic();
        logic.BeginDrag(seconds);

        Assert.True(logic.TryTakePendingTarget(out var target));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void Reset_ClearsAvailabilityAndPendingTarget()
    {
        var logic = new ReplaySeekLogic();
        logic.Update("yes", 120d, 0d, 120d, 0d);
        logic.BeginDrag(45d);

        logic.Reset();

        Assert.False(logic.IsAvailable);
        Assert.Equal(0d, logic.Duration);
        Assert.False(logic.IsDragging);
        Assert.False(logic.TryTakePendingTarget(out _));
    }

    [Theory]
    [InlineData(0d, "00:00")]
    [InlineData(-1d, "00:00")]
    [InlineData(double.NaN, "00:00")]
    [InlineData(double.PositiveInfinity, "00:00")]
    [InlineData(5d, "00:05")]
    [InlineData(65d, "01:05")]
    [InlineData(3599d, "59:59")]
    [InlineData(3600d, "1:00:00")]
    [InlineData(3661d, "1:01:01")]
    public void FormatClock_ClampsAndFormats(double seconds, string expected)
    {
        Assert.Equal(expected, ReplaySeekLogic.FormatClock(seconds));
    }
}
