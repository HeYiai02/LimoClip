using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class MainWindowStatusTextTests
{
    [Fact]
    public void FreeformExited_keeps_original_copy()
    {
        Assert.Equal("已退出自由布局：恢复平铺布局。", MainWindowStatusText.FreeformExited());
    }

    [Theory]
    [InlineData(FreeformPreset.AutoFit, "已应用自由布局预设：自动平铺。")]
    [InlineData(FreeformPreset.Hero, "已应用自由布局预设：主副屏。")]
    [InlineData(FreeformPreset.PictureInPicture, "已应用自由布局预设：画中画。")]
    public void FreeformPresetApplied_maps_each_preset(FreeformPreset preset, string expected)
    {
        Assert.Equal(expected, MainWindowStatusText.FreeformPresetApplied(preset));
    }

    [Theory]
    [InlineData(FreeformPreset.AutoFit, "已进入自由布局：拖动/缩放窗格，右键设置画中画。")]
    [InlineData(FreeformPreset.Hero, "已进入自由布局（主副屏）：拖动/缩放窗格，右键设置画中画。")]
    [InlineData(FreeformPreset.PictureInPicture, "已进入自由布局（画中画）：拖动/缩放窗格，右键可置顶。")]
    public void FreeformEntered_maps_each_preset(FreeformPreset preset, string expected)
    {
        Assert.Equal(expected, MainWindowStatusText.FreeformEntered(preset));
    }

    [Fact]
    public void WorkspaceEmptyHint_keeps_original_copy()
    {
        Assert.Equal("工作区还没有窗格：先双击左侧直播间加入工作区。", MainWindowStatusText.WorkspaceEmptyHint());
    }

    [Fact]
    public void DanmakuSubscribeFailed_includes_message()
    {
        Assert.Equal("弹幕订阅失败：连接被拒绝", MainWindowStatusText.DanmakuSubscribeFailed("连接被拒绝"));
    }

    [Fact]
    public void RoomAlreadyInWorkspace_uses_display_title()
    {
        Assert.Equal("测试直播间 已在工作区", MainWindowStatusText.RoomAlreadyInWorkspace("测试直播间"));
    }

    [Fact]
    public void RoomAddedToWorkspace_uses_display_title()
    {
        Assert.Equal("已放入 测试直播间", MainWindowStatusText.RoomAddedToWorkspace("测试直播间"));
    }

    [Fact]
    public void AutoFitReset_keeps_original_copy()
    {
        Assert.Equal("已重置为自动适配", MainWindowStatusText.AutoFitReset());
    }

    [Theory]
    [InlineData(1, "已自适应 1 路（比例已归位）")]
    [InlineData(4, "已自适应 4 路（比例已归位）")]
    public void AutoFitApplied_includes_room_count(int count, string expected)
    {
        Assert.Equal(expected, MainWindowStatusText.AutoFitApplied(count));
    }

    [Fact]
    public void RatioSnapshotParseFailed_includes_reason()
    {
        Assert.Equal("自定义比例快照解析失败，将重建布局：坏数据",
            MainWindowStatusText.RatioSnapshotParseFailed("坏数据"));
    }

    [Fact]
    public void RatioRestored_keeps_original_copy()
    {
        Assert.Equal("已恢复自定义比例", MainWindowStatusText.RatioRestored());
    }

    [Fact]
    public void RatioRestoredWithRebuild_keeps_original_copy()
    {
        Assert.Equal("已恢复自定义比例（播放器已重建）", MainWindowStatusText.RatioRestoredWithRebuild());
    }

    [Fact]
    public void RoomsSwapped_lists_both_titles()
    {
        Assert.Equal("已交换 A 与 B", MainWindowStatusText.RoomsSwapped("A", "B"));
    }

    [Fact]
    public void FullscreenNoQuality_keeps_original_copy()
    {
        Assert.Equal("全屏失败：当前房间没有可用画质", MainWindowStatusText.FullscreenNoQuality());
    }

    [Fact]
    public void FullscreenEntered_includes_title_and_escape_hint()
    {
        Assert.Equal("全屏：房间A（Esc 退出）", MainWindowStatusText.FullscreenEntered("房间A"));
    }

    [Fact]
    public void FullscreenPlayFailed_includes_reason()
    {
        Assert.Equal("全屏播放失败：超时", MainWindowStatusText.FullscreenPlayFailed("超时"));
    }

    [Fact]
    public void FullscreenExited_keeps_original_copy()
    {
        Assert.Equal("已退出全屏", MainWindowStatusText.FullscreenExited());
    }

    [Fact]
    public void AllMuted_keeps_original_copy()
    {
        Assert.Equal("已全部静音", MainWindowStatusText.AllMuted());
    }

    [Fact]
    public void MuteRestored_keeps_original_copy()
    {
        Assert.Equal("已恢复全部静音前的状态", MainWindowStatusText.MuteRestored());
    }

    [Fact]
    public void RoomUpdated_uses_alias()
    {
        Assert.Equal("已更新 别名", MainWindowStatusText.RoomUpdated("别名"));
    }

    [Fact]
    public void RoomRemoved_uses_title()
    {
        Assert.Equal("已移出 房间B", MainWindowStatusText.RoomRemoved("房间B"));
    }

    [Fact]
    public void PaneSplitAdded_uses_alias()
    {
        Assert.Equal("已拆分新窗格并放入 别名", MainWindowStatusText.PaneSplitAdded("别名"));
    }

    [Fact]
    public void CustomRatioEnabled_keeps_original_copy()
    {
        Assert.Equal("已切换为自定义比例；新增房间只追加，不会重排", MainWindowStatusText.CustomRatioEnabled());
    }

    [Fact]
    public void ProfileDeserializeFailed_includes_profile_and_reason()
    {
        Assert.Equal("方案「方案A」反序列化失败，已重建：坏 JSON",
            MainWindowStatusText.ProfileDeserializeFailed("方案A", "坏 JSON"));
    }
}
