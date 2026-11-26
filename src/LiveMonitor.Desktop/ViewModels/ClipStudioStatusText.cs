using System;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台（含边录边剪）面向用户的状态与提示文案。
/// 集中纯文本模板，便于直接单测并避免文案漂移；调用方只负责赋值。
/// </summary>
internal static class ClipStudioStatusText
{
    public const string EncoderAutoDetecting = "自动探测中…";
    public const string LiveOpening = "正在读取录制中的分片…";
    public const string LiveOpened = "正在录制中，可对已录制部分进行剪辑。";
    public const string LiveCompleted = "录制已结束，已切换为离线剪辑模式。";

    /// <summary>打开 live 场次时录制已进入后续分片：拒绝进入。</summary>
    public static string LiveSegmented(int partNumber) =>
        $"本次录制已发生自动分段（第 {partNumber} 段），边录边剪暂不可用，请等待录制结束后再剪辑。";

    /// <summary>同步时发现录制已进入后续分片：停止边录边剪。</summary>
    public static string LiveSyncStoppedBySegment(int partNumber) =>
        $"本次录制已发生自动分段（当前第 {partNumber} 段），边录边剪已停止，请等待录制结束后再剪辑。";

    public static string LivePartMissing(string? partPath) =>
        $"录制分片尚未生成，请稍后再试：{partPath}";

    public static string LiveOpenFailed(string message) => $"打开录制中分片失败：{message}";

    public static string LiveSyncSucceeded(TimeSpan recordedDuration, TimeSpan safeClipDuration) =>
        $"已同步最新录制进度（已录 {RecordingUiFormat.FormatDuration(recordedDuration)}，安全可剪至 {RecordingUiFormat.FormatDuration(safeClipDuration)}）";

    public static string LiveSyncFailed(string message) => $"同步录制进度失败：{message}";

    public static string AutoSyncFailed(string message) => $"自动同步录制进度失败：{message}";

    public static string LiveCompletedWithoutFile(string? error) =>
        $"录制已结束，但录像文件不可用：{error}";

    public static string LiveSwitchToOfflineFailed(string message) => $"切换离线剪辑失败：{message}";

    public static string LiveDanmakuLoadFailed(string message) =>
        $"增量读取弹幕失败（不影响导出）：{message}";

    public static string LiveWaveformFailed(string message) =>
        $"边录边剪波形提取失败（不影响导出）：{message}";

    /// <summary>硬件编码器探测器返回的名称 → 界面文案。</summary>
    public static string EncoderLabel(string? encoderName) => encoderName switch
    {
        "h264_nvenc" => "NVENC · NVIDIA 硬件编码",
        "h264_qsv" => "QSV · Intel 硬件编码",
        "h264_amf" => "AMF · AMD 硬件编码",
        _ => "libx264 · 软件编码"
    };

    public static string EncoderDetectionFailed(string message) =>
        $"硬件编码器探测失败，已回退软件编码：{message}";
}
