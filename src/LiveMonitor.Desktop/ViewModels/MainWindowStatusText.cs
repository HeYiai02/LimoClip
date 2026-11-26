using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 主窗口状态栏文案：集中自由布局/布局快照/全屏/静音/房间增删等提示，避免文案散落在命令编排里。
/// 仅承载可读文案与自由布局预设分支，不依赖 Avalonia 与窗口状态。
/// </summary>
internal static class MainWindowStatusText
{
    public static string FreeformExited() => "已退出自由布局：恢复平铺布局。";

    public static string FreeformPresetApplied(FreeformPreset preset) => preset switch
    {
        FreeformPreset.Hero => "已应用自由布局预设：主副屏。",
        FreeformPreset.PictureInPicture => "已应用自由布局预设：画中画。",
        _ => "已应用自由布局预设：自动平铺。"
    };

    public static string FreeformEntered(FreeformPreset preset) => preset switch
    {
        FreeformPreset.Hero => "已进入自由布局（主副屏）：拖动/缩放窗格，右键设置画中画。",
        FreeformPreset.PictureInPicture => "已进入自由布局（画中画）：拖动/缩放窗格，右键可置顶。",
        _ => "已进入自由布局：拖动/缩放窗格，右键设置画中画。"
    };

    public static string WorkspaceEmptyHint() => "工作区还没有窗格：先双击左侧直播间加入工作区。";

    public static string DanmakuSubscribeFailed(string message) => $"弹幕订阅失败：{message}";

    public static string RoomAlreadyInWorkspace(string title) => $"{title} 已在工作区";

    public static string RoomAddedToWorkspace(string title) => $"已放入 {title}";

    public static string AutoFitReset() => "已重置为自动适配";

    public static string AutoFitApplied(int count) => $"已自适应 {count} 路（比例已归位）";

    public static string RatioSnapshotParseFailed(string message) => $"自定义比例快照解析失败，将重建布局：{message}";

    public static string RatioRestored() => "已恢复自定义比例";

    public static string RatioRestoredWithRebuild() => "已恢复自定义比例（播放器已重建）";

    public static string RoomsSwapped(string sourceTitle, string targetTitle) => $"已交换 {sourceTitle} 与 {targetTitle}";

    public static string FullscreenNoQuality() => "全屏失败：当前房间没有可用画质";

    public static string FullscreenEntered(string title) => $"全屏：{title}（Esc 退出）";

    public static string FullscreenPlayFailed(string message) => $"全屏播放失败：{message}";

    public static string FullscreenExited() => "已退出全屏";

    public static string AllMuted() => "已全部静音";

    public static string MuteRestored() => "已恢复全部静音前的状态";

    public static string RoomUpdated(string alias) => $"已更新 {alias}";

    public static string RoomRemoved(string title) => $"已移出 {title}";

    public static string PaneSplitAdded(string alias) => $"已拆分新窗格并放入 {alias}";

    public static string CustomRatioEnabled() => "已切换为自定义比例；新增房间只追加，不会重排";

    public static string ProfileDeserializeFailed(string profileName, string message) => $"方案「{profileName}」反序列化失败，已重建：{message}";

    public static string FreeformModeLabel(bool isFreeform) => isFreeform ? "自由布局：开" : "自由布局：关";

    public static string FreeformModeHint(bool isFreeform) => isFreeform
        ? "拖动窗格标题或 Alt+拖动画面移动，边缘/角缩放；右键可置顶画中画或锁定。"
        : "开启后可用自由画布摆放窗格，支持磁吸、缩放与画中画置顶。";

    public static string RecordingEngineInitFailed(string message) => $"录制引擎初始化失败：{message}";

    public static string RecordingCenterRefreshFailed(string message) => $"刷新录制中心失败：{message}";

    public static string ClipStudioInitFailed(string message) => $"初始化剪辑台失败：{message}";

    public static string ClipStudioEntered(string roomDisplayName) => $"已进入剪辑台：{roomDisplayName}";

    public static string ClipStudioOpenFailed(string message) => $"打开剪辑台失败：{message}";

    public const string WorkspaceNotInitialized = "工作区尚未初始化。";

    public const string RoomNotFound = "房间不存在。";

    public static string RoomAdded(string alias) => $"已添加 {alias}";

    public static string AutoFitAppliedShort(int count) => $"已自适应 {count} 路";

    public static string AutoAdaptedShort(int count) => $"已自动适配 {count} 路";

    public const string CustomRatioRestoreProfileName = "自定义比例（恢复）";

    public static string RoomRemovedKeepingRatio(string title) => $"已移出 {title}（保留自定义比例）";

    public const string PlayerReconnecting = "正在重新连接…";

    public const string HeroSideMinimumHint = "房间不足 3 路，主副屏布局暂按单行排布";

    public static string HeroSideApplied(int count) => $"已切换为主副屏布局（{count} 路）";

    public static string RecordingStopped(string alias) => $"已停止 {alias} 的录制。";

    public static string RecordingStopFailedCancelingDelete(string message) => $"停止录制失败，已取消删除：{message}";
}
