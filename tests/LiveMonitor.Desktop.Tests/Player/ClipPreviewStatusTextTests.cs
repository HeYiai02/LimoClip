using System;
using System.Collections.Generic;
using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>剪辑台本地预览遮罩文案（`ClipPreviewStatusText`）的直接单测。</summary>
public sealed class ClipPreviewStatusTextTests
{
    [Theory]
    [InlineData(ClipPreviewStatusText.EmptyTitle, "本地剪辑预览")]
    [InlineData(ClipPreviewStatusText.EmptyDetail, "从左侧列表选择录像，或打开本地文件")]
    [InlineData(ClipPreviewStatusText.LoadingTitle, "加载中")]
    [InlineData(ClipPreviewStatusText.LoadingDetail, "正在读取视频…")]
    [InlineData(ClipPreviewStatusText.FailedTitle, "播放失败")]
    [InlineData(ClipPreviewStatusText.FailedDetail, "请检查文件是否完整")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
        => Assert.Equal(expected, actual);

    public static IEnumerable<object[]> OverlayStates()
    {
        yield return new object[] { ClipPlayerState.Empty, "本地剪辑预览", "从左侧列表选择录像，或打开本地文件" };
        yield return new object[] { ClipPlayerState.Loading, "加载中", "正在读取视频…" };
        yield return new object[] { ClipPlayerState.Failed, "播放失败", "请检查文件是否完整" };
    }

    [Theory]
    [MemberData(nameof(OverlayStates))]
    public void Title_And_Detail_For_OverlayStates(ClipPlayerState state, string title, string detail)
    {
        Assert.Equal(title, ClipPreviewStatusText.Title(state));
        Assert.Equal(detail, ClipPreviewStatusText.Detail(state));
    }

    [Theory]
    [InlineData(ClipPlayerState.Ready)]
    [InlineData(ClipPlayerState.Playing)]
    [InlineData(ClipPlayerState.Paused)]
    [InlineData(ClipPlayerState.Ended)]
    public void NonOverlayStates_ReturnEmptyStrings(ClipPlayerState state)
    {
        Assert.Equal(string.Empty, ClipPreviewStatusText.Title(state));
        Assert.Equal(string.Empty, ClipPreviewStatusText.Detail(state));
    }

    [Fact]
    public void FailureWording_MatchesLiveTileWording()
    {
        // 本地预览与直播窗格的失败标题共用「播放失败」措辞，其余状态各用各的。
        Assert.Equal(RoomStatusText.PlaybackFailed, ClipPreviewStatusText.FailedTitle);
        Assert.NotEqual(RoomStatusText.WaitingForStart, ClipPreviewStatusText.EmptyTitle);
    }

    [Fact]
    public void UnknownState_FallsBackToEmptyStrings()
    {
        var unknown = (ClipPlayerState)99;
        Assert.Equal(string.Empty, ClipPreviewStatusText.Title(unknown));
        Assert.Equal(string.Empty, ClipPreviewStatusText.Detail(unknown));
    }
}
