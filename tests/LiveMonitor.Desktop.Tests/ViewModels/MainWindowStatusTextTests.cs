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

    [Fact]
    public void FreeformMode_uses_two_sided_copy()
    {
        Assert.Equal("自由布局：开", MainWindowStatusText.FreeformModeLabel(true));
        Assert.Equal("自由布局：关", MainWindowStatusText.FreeformModeLabel(false));
        Assert.Contains("Alt+拖动", MainWindowStatusText.FreeformModeHint(true));
        Assert.Contains("磁吸", MainWindowStatusText.FreeformModeHint(false));
    }

    [Fact]
    public void FailureMessages_prefix_reason()
    {
        Assert.Equal("录制引擎初始化失败：坏引擎", MainWindowStatusText.RecordingEngineInitFailed("坏引擎"));
        Assert.Equal("刷新录制中心失败：超时", MainWindowStatusText.RecordingCenterRefreshFailed("超时"));
        Assert.Equal("初始化剪辑台失败：缺文件", MainWindowStatusText.ClipStudioInitFailed("缺文件"));
        Assert.Equal("打开剪辑台失败：无权限", MainWindowStatusText.ClipStudioOpenFailed("无权限"));
        Assert.Equal("停止录制失败，已取消删除：锁占用", MainWindowStatusText.RecordingStopFailedCancelingDelete("锁占用"));
    }

    [Fact]
    public void ClipStudioEntered_uses_room_display_name()
        => Assert.Equal("已进入剪辑台：#4 蛋蛋的BJ", MainWindowStatusText.ClipStudioEntered("#4 蛋蛋的BJ"));

    [Fact]
    public void RoomFlow_messages()
    {
        Assert.Equal("工作区尚未初始化。", MainWindowStatusText.WorkspaceNotInitialized);
        Assert.Equal("房间不存在。", MainWindowStatusText.RoomNotFound);
        Assert.Equal("已添加 别名", MainWindowStatusText.RoomAdded("别名"));
        Assert.Equal("已自适应 4 路", MainWindowStatusText.AutoFitAppliedShort(4));
        Assert.Equal("已自动适配 4 路", MainWindowStatusText.AutoAdaptedShort(4));
        Assert.Equal("自定义比例（恢复）", MainWindowStatusText.CustomRatioRestoreProfileName);
        Assert.Equal("已移出 房间B（保留自定义比例）", MainWindowStatusText.RoomRemovedKeepingRatio("房间B"));
    }

    [Fact]
    public void HeroSide_messages()
    {
        Assert.Equal("正在重新连接…", MainWindowStatusText.PlayerReconnecting);
        Assert.Equal("房间不足 3 路，主副屏布局暂按单行排布", MainWindowStatusText.HeroSideMinimumHint);
        Assert.Equal("已切换为主副屏布局（5 路）", MainWindowStatusText.HeroSideApplied(5));
    }

    [Fact]
    public void RecordingStopped_uses_alias()
        => Assert.Equal("已停止 别名 的录制。", MainWindowStatusText.RecordingStopped("别名"));
}
