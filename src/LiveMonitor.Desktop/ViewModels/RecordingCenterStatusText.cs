namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心与历史录像子视图模型的状态栏提示文案（V0.7.5 集中管理，避免同一文案多处漂移）。
/// 全部文案与抽离前逐字一致。
/// </summary>
internal static class RecordingCenterStatusText
{
    // ---- 录制中心主视图 ----

    public static string RefreshFailed(string message) => $"刷新录制中心失败：{message}";

    public static string GlobalDirectoryPickerMissing() => "当前界面未接入目录选择器，无法修改全局目录。";

    public static string DirectoryPickerFailed(string message) => $"打开目录选择器失败：{message}";

    public static string GlobalDirectoryUpdated(string directory) => $"全局录像目录已更新：{directory}（新开始的录制生效）";

    public static string GlobalDirectoryUpdateFailed(string message) => $"修改全局录像目录失败：{message}";

    public static string DirectoryOpened(string directory) => $"已打开目录：{directory}";

    public static string OpenDirectoryFailed(string message, string directory)
        => $"打开目录失败：{message}。请手动在资源管理器中打开：{directory}";

    public static string RoomDirectoryPickerMissing() => "当前界面未接入目录选择器，无法设置独立目录。";

    public static string RoomDirectoryUpdated(string displayTitle, string directory)
        => $"「{displayTitle}」的独立保存目录已设置为：{directory}";

    public static string RoomDirectoryUpdateFailed(string message) => $"设置独立目录失败：{message}";

    public static string RoomDirectoryCleared(string displayTitle, string globalDirectory)
        => $"「{displayTitle}」已改为跟随全局目录：{globalDirectory}";

    public static string RoomDirectoryClearFailed(string message) => $"清除独立目录失败：{message}";

    public static string RecordingStarted(string roomDisplayName, string videoFilePath)
        => $"开始录制：{roomDisplayName} → {videoFilePath}";

    // ---- 历史录像子视图模型 ----

    public static string HistoryRefreshFailed(string message) => $"读取录制历史失败：{message}";

    public static string HistoryFileLocated(string fileName) => $"已在资源管理器中定位：{fileName}";

    public static string HistoryFileLocateFailed(string? error, string path)
        => $"定位录像文件失败：{error}。请手动在资源管理器中打开：{path}";

    public static string HistoryFileMissingOpened(string directory) => $"录像文件不存在，已打开所在目录：{directory}";

    public static string HistoryFolderOpenFailed(string message) => $"打开文件夹失败：{message}";

    public static string HistoryFileOrDirectoryMissing() => "找不到录像文件或所在目录（可能已被手动删除）。";

    public static string HistoryDeleteRejectedActive() => "该录制仍在进行中，请先停止录制再删除记录。";

    public static string HistoryDeleteConfirmMissing() => "当前界面未接入删除确认弹窗，已取消删除。";

    public static string HistoryDeleteConfirmFailed(string message) => $"删除确认失败：{message}";

    public static string HistoryDeleted(string roomTitle, string startedAtText) => $"已删除录制记录：{roomTitle} · {startedAtText}";


    public static string HistoryRetryMergeSucceeded(string roomTitle, string? warning)
        => string.IsNullOrWhiteSpace(warning)
            ? $"已重试合并：{roomTitle}"
            : $"已重试合并：{roomTitle}（{warning}）";

    public static string HistoryRetryMergeFailed(string message) => $"重试合并失败：{message}";
    public static string HistoryDeleteFailed(string message) => $"删除录制记录失败：{message}";

    public static string ClipRejectedActive() => "该录制仍在进行中，请先停止录制再进入剪辑台。";

    public static string ClipFileMissing(string? videoFilePath) => $"录像文件不存在，无法剪辑：{videoFilePath}";

    public static string ClipStudioMissing() => "当前界面未接入剪辑台。";

    public static string OpenClipStudioFailed(string message) => $"打开剪辑台失败：{message}";

    public static string LiveClipJustEnded() => "该录制刚刚结束，正在刷新录制列表…";
}
