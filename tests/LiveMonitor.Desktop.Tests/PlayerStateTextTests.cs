using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class PlayerStateTextTests
{
    [Theory]
    [InlineData(PlayerState.Loading, "连接中")]
    [InlineData(PlayerState.Playing, "直播中")]
    [InlineData(PlayerState.Offline, "已停止")]
    [InlineData(PlayerState.Reconnecting, "重连中")]
    [InlineData(PlayerState.Failed, "播放失败")]
    public void Describe_MapsAllStates(PlayerState state, string expected)
        => Assert.Equal(expected, PlayerStateText.Describe(state));

    [Fact]
    public void Describe_Reconnecting_IsNotOfflineText()
    {
        // 断流重连不能被渲染成「已停止/未开播」：主播可能仍在直播。
        Assert.NotEqual(PlayerStateText.Describe(PlayerState.Offline), PlayerStateText.Describe(PlayerState.Reconnecting));
    }
}
