using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RoomStatusTextTests
{
    [Theory]
    [InlineData(RoomStatusText.EmptyPane, "空窗格")]
    [InlineData(RoomStatusText.EmptyPaneDetail, "从左侧双击直播间，或拖入此窗格")]
    [InlineData(RoomStatusText.WaitingForStart, "等待起播")]
    [InlineData(RoomStatusText.ReadingStatus, "正在读取房间状态…")]
    [InlineData(RoomStatusText.Live, "直播中")]
    [InlineData(RoomStatusText.Offline, "未开播")]
    [InlineData(RoomStatusText.PlaybackFailed, "播放失败")]
    [InlineData(RoomStatusText.Reconnecting, "重连中")]
    [InlineData(RoomStatusText.AutoReconnectDetail, "直播流已中断，正在自动重连…")]
    [InlineData(RoomStatusText.StreamInterrupted, "直播流已中断")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OfflineDetail_UsesPollDelayInSeconds()
    {
        Assert.Equal(
            "主播未开播，60 秒后重试",
            RoomStatusText.OfflineDetail(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void FailureRecheckDetail_UsesReconnectPolicyDelay()
    {
        Assert.Equal(
            "直播流已中断，60 秒后自动重试",
            RoomStatusText.FailureRecheckDetail(ReconnectPolicy.FailureRecheckDelay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.2)]
    [InlineData(0.9)]
    public void Details_RoundSubSecondDelaysUpToOneSecond(double seconds)
    {
        var delay = TimeSpan.FromSeconds(seconds);

        Assert.Equal("主播未开播，1 秒后重试", RoomStatusText.OfflineDetail(delay));
        Assert.Equal("直播流已中断，1 秒后自动重试", RoomStatusText.FailureRecheckDetail(delay));
    }

    [Fact]
    public void Details_RoundFractionalSecondsUp()
    {
        Assert.Equal(
            "主播未开播，31 秒后重试",
            RoomStatusText.OfflineDetail(TimeSpan.FromSeconds(30.1)));
        Assert.Equal(
            "直播流已中断，11 秒后自动重试",
            RoomStatusText.FailureRecheckDetail(TimeSpan.FromSeconds(10.5)));
    }

    [Fact]
    public void TileStateTexts_AreDistinct()
    {
        var states = new[]
        {
            RoomStatusText.EmptyPane,
            RoomStatusText.WaitingForStart,
            RoomStatusText.Live,
            RoomStatusText.Offline,
            RoomStatusText.PlaybackFailed,
            RoomStatusText.Reconnecting,
        };

        Assert.Equal(states.Length, states.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(PlayerState.Playing)]
    [InlineData(PlayerState.Reconnecting)]
    [InlineData(PlayerState.Failed)]
    public void PlayerStateText_ReusesRoomStatusWording(PlayerState state)
    {
        var expected = state switch
        {
            PlayerState.Playing => RoomStatusText.Live,
            PlayerState.Reconnecting => RoomStatusText.Reconnecting,
            _ => RoomStatusText.PlaybackFailed,
        };

        Assert.Equal(expected, PlayerStateText.Describe(state));
    }
}
