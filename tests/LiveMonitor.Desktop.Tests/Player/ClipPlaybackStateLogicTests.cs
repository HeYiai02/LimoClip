using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>剪辑台预览播放器状态机与守卫规则的纯逻辑覆盖。</summary>
public sealed class ClipPlaybackStateLogicTests
{
    [Theory]
    [InlineData(ClipPlayerState.Ready, true)]
    [InlineData(ClipPlayerState.Playing, true)]
    [InlineData(ClipPlayerState.Paused, true)]
    [InlineData(ClipPlayerState.Ended, true)]
    [InlineData(ClipPlayerState.Empty, false)]
    [InlineData(ClipPlayerState.Loading, false)]
    [InlineData(ClipPlayerState.Failed, false)]
    public void IsLoadedState_MatchesLoadedStates(ClipPlayerState state, bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.IsLoadedState(state));
    }

    [Theory]
    [InlineData(ClipPlayerState.Ready, false)]
    [InlineData(ClipPlayerState.Playing, false)]
    [InlineData(ClipPlayerState.Paused, false)]
    [InlineData(ClipPlayerState.Ended, true)]
    public void ShouldRestartBeforePlay_OnlyFromEnded(ClipPlayerState state, bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.ShouldRestartBeforePlay(state));
    }

    [Theory]
    [InlineData(ClipPlayerState.Playing, true)]
    [InlineData(ClipPlayerState.Paused, false)]
    [InlineData(ClipPlayerState.Ready, false)]
    [InlineData(ClipPlayerState.Ended, false)]
    public void CanPause_OnlyWhilePlaying(ClipPlayerState state, bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.CanPause(state));
    }

    [Theory]
    [InlineData(ClipPlayerState.Ended, true)]
    [InlineData(ClipPlayerState.Paused, false)]
    [InlineData(ClipPlayerState.Playing, false)]
    public void ShouldReturnToPausedAfterSeek_OnlyFromEnded(ClipPlayerState state, bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.ShouldReturnToPausedAfterSeek(state));
    }

    [Theory]
    [InlineData(ClipPlayerState.Playing, false, true)]
    [InlineData(ClipPlayerState.Paused, false, true)]
    [InlineData(ClipPlayerState.Playing, true, false)]
    [InlineData(ClipPlayerState.Ready, false, false)]
    [InlineData(ClipPlayerState.Ended, false, false)]
    [InlineData(ClipPlayerState.Loading, false, false)]
    [InlineData(ClipPlayerState.Failed, false, false)]
    public void CanRaiseCompletion_OnlyOnceFromPlayingOrPaused(
        ClipPlayerState state,
        bool completionRaised,
        bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.CanRaiseCompletion(state, completionRaised));
    }

    [Theory]
    [InlineData(ClipPlayerState.Ready, ClipPlayerState.Ready, false)]
    [InlineData(ClipPlayerState.Ready, ClipPlayerState.Playing, true)]
    [InlineData(ClipPlayerState.Failed, ClipPlayerState.Failed, true)]
    [InlineData(ClipPlayerState.Loading, ClipPlayerState.Empty, true)]
    public void ShouldRaiseStateChanged_AllowsFailedRepeat(
        ClipPlayerState current,
        ClipPlayerState next,
        bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.ShouldRaiseStateChanged(current, next));
    }

    [Theory]
    [InlineData(120, 30, 120)]
    [InlineData(120, 120, 120)]
    [InlineData(0, 30, 30)]
    [InlineData(-5, 30, 30)]
    public void CompletionPosition_UsesDurationWhenKnown(int durationSeconds, int positionSeconds, int expectedSeconds)
    {
        var result = ClipPlaybackStateLogic.CompletionPosition(
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromSeconds(positionSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Theory]
    [InlineData(0.2, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(8.0, 2.0)]
    public void ClampSpeed_ClampsToSupportedRange(double speed, double expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.ClampSpeed(speed), 6);
    }

    [Fact]
    public void ClampSpeed_NaNPassesThroughLikeMathClamp()
    {
        Assert.True(double.IsNaN(ClipPlaybackStateLogic.ClampSpeed(double.NaN)));
    }

    [Fact]
    public void BuildMissingFileMessage_IncludesPath()
    {
        Assert.Equal(
            "找不到视频文件：E:\\clips\\a.mkv",
            ClipPlaybackStateLogic.BuildMissingFileMessage("E:\\clips\\a.mkv"));
    }

    [Fact]
    public void BuildPlaybackFailedMessage_IncludesErrorCode()
    {
        Assert.Equal("本地视频播放失败 (code -12)", ClipPlaybackStateLogic.BuildPlaybackFailedMessage(-12));
    }

    [Theory]
    [InlineData(ClipPlayerState.Loading, true)]
    [InlineData(ClipPlayerState.Ready, false)]
    [InlineData(ClipPlayerState.Empty, false)]
    public void ShouldShowPreparingDetail_OnlyWhileLoading(ClipPlayerState state, bool expected)
    {
        Assert.Equal(expected, ClipPlaybackStateLogic.ShouldShowPreparingDetail(state));
    }

    [Theory]
    [InlineData((int)ClipEndReason.Stop, true, false, (int)ClipEndAction.Ignore)]
    [InlineData((int)ClipEndReason.Quit, true, false, (int)ClipEndAction.Ignore)]
    [InlineData((int)ClipEndReason.Error, false, true, (int)ClipEndAction.FailWhileLoading)]
    [InlineData((int)ClipEndReason.Error, true, false, (int)ClipEndAction.Fail)]
    [InlineData((int)ClipEndReason.Error, false, false, (int)ClipEndAction.Fail)]
    [InlineData((int)ClipEndReason.Eof, true, false, (int)ClipEndAction.Complete)]
    [InlineData((int)ClipEndReason.Eof, false, false, (int)ClipEndAction.Ignore)]
    [InlineData((int)ClipEndReason.Other, true, false, (int)ClipEndAction.Ignore)]
    public void ClassifyEnd_MapsReasonToAction(
        int reason,
        bool isLoaded,
        bool isLoading,
        int expected)
    {
        Assert.Equal(
            (ClipEndAction)expected,
            ClipPlaybackStateLogic.ClassifyEnd((ClipEndReason)reason, isLoaded, isLoading));
    }

    [Fact]
    public void Constants_StayStableForViewContracts()
    {
        Assert.Equal(0.5, ClipPlaybackStateLogic.MinSpeed);
        Assert.Equal(2.0, ClipPlaybackStateLogic.MaxSpeed);
        Assert.Equal("正在加载本地视频…", ClipPlaybackStateLogic.LoadingDetail);
        Assert.Equal("正在准备解码…", ClipPlaybackStateLogic.PreparingDetail);
        Assert.Equal("已取消加载", ClipPlaybackStateLogic.CancelledDetail);
    }
}
