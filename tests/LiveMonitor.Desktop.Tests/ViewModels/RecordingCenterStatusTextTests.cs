using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class RecordingCenterStatusTextTests
{
    [Fact]
    public void RefreshFailed_includes_message()
        => Assert.Equal("刷新录制中心失败：boom", RecordingCenterStatusText.RefreshFailed("boom"));

    [Fact]
    public void GlobalDirectoryPickerMissing_keeps_original_copy()
        => Assert.Equal("当前界面未接入目录选择器，无法修改全局目录。", RecordingCenterStatusText.GlobalDirectoryPickerMissing());

    [Fact]
    public void DirectoryPickerFailed_includes_message()
        => Assert.Equal("打开目录选择器失败：boom", RecordingCenterStatusText.DirectoryPickerFailed("boom"));

    [Fact]
    public void GlobalDirectoryUpdated_includes_directory_and_effect_note()
        => Assert.Equal("全局录像目录已更新：D:\\clips（新开始的录制生效）", RecordingCenterStatusText.GlobalDirectoryUpdated("D:\\clips"));

    [Fact]
    public void GlobalDirectoryUpdateFailed_includes_message()
        => Assert.Equal("修改全局录像目录失败：boom", RecordingCenterStatusText.GlobalDirectoryUpdateFailed("boom"));

    [Fact]
    public void DirectoryOpened_includes_directory()
        => Assert.Equal("已打开目录：D:\\clips", RecordingCenterStatusText.DirectoryOpened("D:\\clips"));

    [Fact]
    public void OpenDirectoryFailed_includes_message_and_path()
        => Assert.Equal("打开目录失败：boom。请手动在资源管理器中打开：D:\\clips",
            RecordingCenterStatusText.OpenDirectoryFailed("boom", "D:\\clips"));

    [Fact]
    public void RoomDirectoryPickerMissing_keeps_original_copy()
        => Assert.Equal("当前界面未接入目录选择器，无法设置独立目录。", RecordingCenterStatusText.RoomDirectoryPickerMissing());

    [Fact]
    public void RoomDirectoryUpdated_wraps_title_in_brackets()
        => Assert.Equal("「房间A」的独立保存目录已设置为：D:\\clips",
            RecordingCenterStatusText.RoomDirectoryUpdated("房间A", "D:\\clips"));

    [Fact]
    public void RoomDirectoryUpdateFailed_includes_message()
        => Assert.Equal("设置独立目录失败：boom", RecordingCenterStatusText.RoomDirectoryUpdateFailed("boom"));

    [Fact]
    public void RoomDirectoryCleared_wraps_title_and_shows_global()
        => Assert.Equal("「房间A」已改为跟随全局目录：D:\\global",
            RecordingCenterStatusText.RoomDirectoryCleared("房间A", "D:\\global"));

    [Fact]
    public void RoomDirectoryClearFailed_includes_message()
        => Assert.Equal("清除独立目录失败：boom", RecordingCenterStatusText.RoomDirectoryClearFailed("boom"));

    [Fact]
    public void RecordingStarted_uses_arrow_between_room_and_path()
        => Assert.Equal("开始录制：房间A → D:\\clips\\a.mkv",
            RecordingCenterStatusText.RecordingStarted("房间A", "D:\\clips\\a.mkv"));

    [Fact]
    public void HistoryRefreshFailed_includes_message()
        => Assert.Equal("读取录制历史失败：boom", RecordingCenterStatusText.HistoryRefreshFailed("boom"));

    [Fact]
    public void HistoryFileLocated_includes_file_name()
        => Assert.Equal("已在资源管理器中定位：a.mkv", RecordingCenterStatusText.HistoryFileLocated("a.mkv"));

    [Fact]
    public void HistoryFileLocateFailed_includes_error_and_path()
        => Assert.Equal("定位录像文件失败：access denied。请手动在资源管理器中打开：D:\\a.mkv",
            RecordingCenterStatusText.HistoryFileLocateFailed("access denied", "D:\\a.mkv"));

    [Fact]
    public void HistoryFileLocateFailed_allows_null_error()
        => Assert.Equal("定位录像文件失败：。请手动在资源管理器中打开：D:\\a.mkv",
            RecordingCenterStatusText.HistoryFileLocateFailed(null, "D:\\a.mkv"));

    [Fact]
    public void HistoryFileMissingOpened_includes_directory()
        => Assert.Equal("录像文件不存在，已打开所在目录：D:\\clips",
            RecordingCenterStatusText.HistoryFileMissingOpened("D:\\clips"));

    [Fact]
    public void HistoryFolderOpenFailed_includes_message()
        => Assert.Equal("打开文件夹失败：boom", RecordingCenterStatusText.HistoryFolderOpenFailed("boom"));

    [Fact]
    public void HistoryFileOrDirectoryMissing_keeps_original_copy()
        => Assert.Equal("找不到录像文件或所在目录（可能已被手动删除）。", RecordingCenterStatusText.HistoryFileOrDirectoryMissing());

    [Fact]
    public void HistoryDeleteRejectedActive_keeps_original_copy()
        => Assert.Equal("该录制仍在进行中，请先停止录制再删除记录。", RecordingCenterStatusText.HistoryDeleteRejectedActive());

    [Fact]
    public void HistoryDeleteConfirmMissing_keeps_original_copy()
        => Assert.Equal("当前界面未接入删除确认弹窗，已取消删除。", RecordingCenterStatusText.HistoryDeleteConfirmMissing());

    [Fact]
    public void HistoryDeleteConfirmFailed_includes_message()
        => Assert.Equal("删除确认失败：boom", RecordingCenterStatusText.HistoryDeleteConfirmFailed("boom"));

    [Fact]
    public void HistoryDeleted_separates_room_and_started_at()
        => Assert.Equal("已删除录制记录：房间A · 2026-01-02 03:04:05",
            RecordingCenterStatusText.HistoryDeleted("房间A", "2026-01-02 03:04:05"));

    [Fact]
    public void HistoryDeleteFailed_includes_message()
        => Assert.Equal("删除录制记录失败：boom", RecordingCenterStatusText.HistoryDeleteFailed("boom"));

    [Fact]
    public void ClipRejectedActive_keeps_original_copy()
        => Assert.Equal("该录制仍在进行中，请先停止录制再进入剪辑台。", RecordingCenterStatusText.ClipRejectedActive());

    [Fact]
    public void ClipFileMissing_includes_path()
        => Assert.Equal("录像文件不存在，无法剪辑：D:\\a.mkv", RecordingCenterStatusText.ClipFileMissing("D:\\a.mkv"));

    [Fact]
    public void ClipStudioMissing_keeps_original_copy()
        => Assert.Equal("当前界面未接入剪辑台。", RecordingCenterStatusText.ClipStudioMissing());

    [Fact]
    public void OpenClipStudioFailed_includes_message()
        => Assert.Equal("打开剪辑台失败：boom", RecordingCenterStatusText.OpenClipStudioFailed("boom"));

    [Fact]
    public void LiveClipJustEnded_keeps_original_copy()
        => Assert.Equal("该录制刚刚结束，正在刷新录制列表…", RecordingCenterStatusText.LiveClipJustEnded());
}
