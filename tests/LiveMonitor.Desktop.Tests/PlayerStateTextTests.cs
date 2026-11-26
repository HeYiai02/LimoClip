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

    [Theory]
    [InlineData(PlayerState.Loading, true, true, false, "加载中", "正在解析直播流…")]
    [InlineData(PlayerState.Offline, true, false, false, "未开播", "等待主播开播")]
    [InlineData(PlayerState.Reconnecting, true, true, true, "直播流中断", "正在自动重连…")]
    [InlineData(PlayerState.Failed, true, false, true, "播放失败", "可点击重新连接")]
    public void Overlay_MapsStateToVisuals(
        PlayerState state,
        bool showOverlay,
        bool showLoadingBar,
        bool showRetry,
        string title,
        string detail)
    {
        var overlay = PlayerStateText.Overlay(state);
        Assert.Equal(showOverlay, overlay.ShowOverlay);
        Assert.Equal(showLoadingBar, overlay.ShowLoadingBar);
        Assert.Equal(showRetry, overlay.ShowRetry);
        Assert.Equal(title, overlay.Title);
        Assert.Equal(detail, overlay.Detail);
    }

    [Theory]
    [InlineData(PlayerState.Playing)]
    [InlineData(PlayerState.Loading)]
    [InlineData(PlayerState.Offline)]
    [InlineData(PlayerState.Reconnecting)]
    [InlineData(PlayerState.Failed)]
    public void Overlay_ShowsOverlayForEveryStateExceptPlaying(PlayerState state)
    {
        var overlay = PlayerStateText.Overlay(state);
        Assert.Equal(state != PlayerState.Playing, overlay.ShowOverlay);
    }

    [Fact]
    public void Overlay_Playing_HasNoChrome()
    {
        var overlay = PlayerStateText.Overlay(PlayerState.Playing);
        Assert.False(overlay.ShowOverlay);
        Assert.False(overlay.ShowLoadingBar);
        Assert.False(overlay.ShowRetry);
        Assert.Equal(string.Empty, overlay.Title);
        Assert.Equal(string.Empty, overlay.Detail);
    }
}
