using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心「历史录像」一行的展示规则：房间快照名回退、汇总文案、删除/剪辑入口的
/// 可见性与悬浮提示、文件名与错误信息判定。纯函数，便于单测。
/// </summary>
internal static class RecordingHistoryDisplayLogic
{
    public const string LiveClipTip = "边录边剪：对正在写入的录像实时剪辑，无需等录制结束";

    public const string DeleteActiveTip = "该录制仍在进行中，请先停止录制再删除记录";

    public const string DeleteReadyTip = "删除记录并同时删除对应的 .mkv 与 .jsonl 文件";

    public const string ClipActiveTip = "该录制仍在进行中，请先停止录制再进入剪辑台";

    public const string ClipMissingTip = "录像文件不存在，无法进入剪辑台";

    public const string RetryMergeTip = "按 .partNNN 分片重新合并一次（合并失败的录像可尝试恢复）";

    public const string ClipReadyTip = "进入剪辑台，预览/裁剪并导出 MP4";

    /// <summary>房间快照名为空时回退到「房间 {RoomId}」（RoomId 为平台侧字符串）。</summary>
    public static string RoomTitle(string? roomDisplayName, string? roomId)
        => string.IsNullOrWhiteSpace(roomDisplayName) ? $"房间 {roomId}" : roomDisplayName;

    /// <summary>正在录制的会话不允许删除或进入剪辑台（RecordingManager 也会拒绝）。</summary>
    public static bool IsActive(RecordingStatus status)
        => status is RecordingStatus.Starting
            or RecordingStatus.Recording
            or RecordingStatus.Stopping;

    public static bool CanDelete(RecordingStatus status) => !IsActive(status);

    /// <summary>已结束且带错误信息的会话（合并失败/崩溃恢复）可以按分片重试合并。</summary>
    public static bool CanRetryMerge(RecordingStatus status, string? errorMessage)
        => !IsActive(status) && !string.IsNullOrWhiteSpace(errorMessage);

    public static string DeleteTip(RecordingStatus status)
        => IsActive(status) ? DeleteActiveTip : DeleteReadyTip;

    public static string Summary(string durationText, string fileSizeText, string danmakuCountText, string highlightCountText)
        => $"时长 {durationText} · 大小 {fileSizeText} · 弹幕 {danmakuCountText} 条 · 高能 {highlightCountText}";

    /// <summary>V0.4：录制已结束、存在视频路径且 .mkv 仍在磁盘上时才能进入剪辑台。</summary>
    public static bool CanClip(RecordingStatus status, string? videoFilePath, bool fileExists)
        => !IsActive(status) && !string.IsNullOrWhiteSpace(videoFilePath) && fileExists;

    /// <summary>历史行「去剪辑」按钮的可见性：录制中的行由边录边剪入口替代。</summary>
    public static bool CanShowClip(RecordingStatus status) => !IsActive(status);

    public static string ClipTip(RecordingStatus status, bool canClip)
    {
        if (IsActive(status))
        {
            return ClipActiveTip;
        }

        return canClip ? ClipReadyTip : ClipMissingTip;
    }

    public static string FileName(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);

    public static bool HasError(string? errorMessage) => !string.IsNullOrWhiteSpace(errorMessage);
}
