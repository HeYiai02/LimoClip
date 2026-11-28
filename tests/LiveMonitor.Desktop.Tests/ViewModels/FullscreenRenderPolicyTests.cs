using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class FullscreenRenderPolicyTests
{
    [Fact]
    public void Normal_workspace_shows_dock_and_hides_freeform()
    {
        Assert.True(FullscreenRenderPolicy.ShouldShowDock(isWorkspaceEmpty: false, isFreeformMode: false, isFocused: false));
        Assert.False(FullscreenRenderPolicy.ShouldShowFreeform(isFreeformMode: false, isFocused: false));
    }

    [Fact]
    public void Freeform_mode_shows_canvas_instead_of_dock()
    {
        Assert.False(FullscreenRenderPolicy.ShouldShowDock(isWorkspaceEmpty: false, isFreeformMode: true, isFocused: false));
        Assert.True(FullscreenRenderPolicy.ShouldShowFreeform(isFreeformMode: true, isFocused: false));
    }

    [Fact]
    public void Empty_workspace_never_shows_dock()
    {
        Assert.False(FullscreenRenderPolicy.ShouldShowDock(isWorkspaceEmpty: true, isFreeformMode: false, isFocused: false));
    }

    [Fact]
    public void Focus_hides_both_hosts()
    {
        Assert.False(FullscreenRenderPolicy.ShouldShowDock(isWorkspaceEmpty: false, isFreeformMode: false, isFocused: true));
        Assert.False(FullscreenRenderPolicy.ShouldShowFreeform(isFreeformMode: true, isFocused: true));
    }

    [Fact]
    public void Focus_stops_pushing_danmaku_to_workspace_tiles()
    {
        Assert.True(FullscreenRenderPolicy.ShouldPushDanmakuToTiles(isFocused: false));
        Assert.False(FullscreenRenderPolicy.ShouldPushDanmakuToTiles(isFocused: true));
    }

    [Fact]
    public void Focus_while_freeform_is_hidden_as_well()
    {
        Assert.False(FullscreenRenderPolicy.ShouldShowFreeform(isFreeformMode: true, isFocused: true));
        Assert.True(FullscreenRenderPolicy.ShouldShowFreeform(isFreeformMode: true, isFocused: false));
    }
}
