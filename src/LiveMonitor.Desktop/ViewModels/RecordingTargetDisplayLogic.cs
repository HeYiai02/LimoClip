using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心「录制目标」行的展示规则：状态文案/颜色、按钮外观、实时指标文本。
/// </summary>
internal static class RecordingTargetDisplayLogic
{
    public const string RecordingStatusText = "录制中";
    public const string RecordingStatusBrush = "#F87171";

    public const string IdleStatusText = "未录制";
    public const string IdleStatusBrush = "#5B6472";

    public const string AutoRecordIdleStatusText = "待开播（自动录制）";
    public const string AutoRecordIdleStatusBrush = "#FBBF24";

    public const string IdleButtonText = "立即开录";
    public const string StopButtonText = "停止录制";
    public const string BusyButtonText = "处理中…";

    public const string IdleButtonBackground = "#1D3A5F";
    public const string IdleButtonForeground = "#BFDBFE";
    public const string RecordingButtonBackground = "#3B1D22";
    public const string RecordingButtonForeground = "#FCA5A5";

    public const string MissingText = "--";
    public const string ZeroText = "0";

    /// <summary>加载中/处理中状态禁止操作。</summary>
    public static bool CanOperate(bool isBusy) => !isBusy;

    public static string StartStopText(bool isBusy, bool isRecording)
        => isBusy ? BusyButtonText : isRecording ? StopButtonText : IdleButtonText;

    public static string StartStopBackground(bool isRecording)
        => isRecording ? RecordingButtonBackground : IdleButtonBackground;

    public static string StartStopForeground(bool isRecording)
        => isRecording ? RecordingButtonForeground : IdleButtonForeground;

    /// <summary>正在录制时由红色竖条/状态胶囊表达，不再重复显示「直播中」徽章。</summary>
    public static bool ShowLiveBadge(bool isLive, bool isRecording) => isLive && !isRecording;

    /// <summary>空闲时的状态文案与颜色：开启自动录制时提示「待开播」。</summary>
    public static (string Text, string Brush) IdleStatus(bool autoRecordEnabled)
        => autoRecordEnabled
            ? (AutoRecordIdleStatusText, AutoRecordIdleStatusBrush)
            : (IdleStatusText, IdleStatusBrush);

    public static (string Text, string Brush) RecordingStatus()
        => (RecordingStatusText, RecordingStatusBrush);

    public static (string Text, string Brush) CompletedStatus(RecordingStatus status)
        => (RecordingUiFormat.StatusText(status), RecordingUiFormat.StatusBrush(status));

    public static MetricsTexts Empty { get; } = new(MissingText, MissingText, MissingText, ZeroText, ZeroText);

    public static MetricsTexts FromProgress(RecordingProgress progress) => new(
        RecordingUiFormat.FormatDuration(progress.Duration),
        RecordingUiFormat.FormatBitrate(progress.BitrateKbps),
        RecordingUiFormat.FormatBytes(progress.FileSizeBytes),
        progress.DanmakuCount.ToString(),
        progress.HighlightCount.ToString());

    /// <summary>刚开始录制：时长从 0 起算，码率等待 ffmpeg 首帧报告。</summary>
    public static MetricsTexts FromSessionStarted(RecordingSession session) => new(
        RecordingUiFormat.FormatDuration(TimeSpan.Zero),
        MissingText,
        RecordingUiFormat.FormatBytes(session.FileSizeBytes),
        session.DanmakuCount.ToString(),
        session.HighlightCount.ToString());

    /// <summary>收尾完成：保留本次录制的最终指标，码率不再有意义。</summary>
    public static MetricsTexts FromSessionCompleted(RecordingSession session) => new(
        RecordingUiFormat.FormatDuration(session.DurationSeconds),
        MissingText,
        RecordingUiFormat.FormatBytes(session.FileSizeBytes),
        session.DanmakuCount.ToString(),
        session.HighlightCount.ToString());
}

/// <summary>录制目标行五个指标格子的文本。</summary>
internal readonly record struct MetricsTexts(
    string Duration,
    string Bitrate,
    string FileSize,
    string DanmakuCount,
    string HighlightCount);
