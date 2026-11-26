namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑导出面板的状态栏文案与按钮/提示文字（V0.7.5 集中管理，避免同一文案多处漂移）。
/// 全部文案与集中前逐字一致。
/// </summary>
internal static class ClipExportStatusText
{
    /// <summary>可导出时的按钮文字。</summary>
    public const string ExportButtonIdle = "开始导出 MP4";

    /// <summary>导出进行中的按钮文字。</summary>
    public const string ExportButtonBusy = "导出中…";

    /// <summary>关键帧对齐误差提示。</summary>
    public const string KeyframeTip = "无弹幕版使用 -c copy 无损切割，入点会自动对齐到最近关键帧，可能提前 1~3 秒。";

    public const string MissingInputs = "请先加载视频、设置有效入出点与输出目录。";

    public const string SafetyBufferBuilding = "安全缓冲生成中（已录不足 5 秒），请稍后再导出。";

    public const string InPointBeyondSafeRange = "当前入点已超过安全可剪范围，请把入点移到录制进度稍早的位置。";

    public const string SafetyBufferReserved = "已自动保留 5 秒安全缓冲避免影响录制。";

    public const string Canceled = "导出已取消。";

    public const string Canceling = "正在取消导出…";

    public const string DirectoryPickerMissing = "当前界面未接入目录选择器。";

    public const string OutputDirectoryMissing = "输出目录尚不存在，导出时会自动创建。";

    public static string Started(string rangeText, string modeText) => $"开始导出：{rangeText} · {modeText}";

    public static string Completed(string outputText) => $"导出完成：{outputText}";

    public static string Failed(string? errorMessage) => $"导出失败：{errorMessage}";

    public static string PickDirectoryFailed(string message) => $"选择导出目录失败：{message}";

    public static string OpenDirectoryFailed(string message, string directory)
        => $"打开输出目录失败：{message}。请手动在资源管理器中打开：{directory}";
}
