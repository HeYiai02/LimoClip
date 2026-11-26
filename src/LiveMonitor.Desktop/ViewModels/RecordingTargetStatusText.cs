namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制目标行向录制中心状态栏回报的文案模板（V0.7.5 集中）。
/// </summary>
internal static class RecordingTargetStatusText
{
    public static string AutoRecordChanged(string displayTitle, bool enabled) =>
        $"「{displayTitle}」自动录制已{(enabled ? "开启" : "关闭")}。";

    public static string AutoRecordUpdateFailed(string displayTitle, string message) =>
        $"更新自动录制失败（{displayTitle}）：{message}";

    public static string RecordingStarting(string displayTitle) =>
        $"正在启动录制「{displayTitle}」…";

    public static string RecordingStarted(string displayTitle, string videoFilePath) =>
        $"「{displayTitle}」已开始录制，文件将写入 {videoFilePath}";

    public static string RecordingStartFailed(string displayTitle, string message) =>
        $"开录失败（{displayTitle}）：{message}";

    public static string RecordingStopping(string displayTitle) =>
        $"正在停止录制「{displayTitle}」…";

    public static string RecordingStopped(string displayTitle) =>
        $"「{displayTitle}」录制已停止。";

    public static string RecordingStopFailed(string displayTitle, string message) =>
        $"停止录制失败（{displayTitle}）：{message}";
}
