using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台「导出队列」一行的展示规则：文件名、模式文案、时间区间、阶段文案、
/// 进度钳制、速度文本与输出文件拼接。纯函数不依赖 Avalonia/导出器。
/// </summary>
internal static class ClipExportTaskDisplayLogic
{
    public const string WaitingState = "等待导出";
    public const string PreparingState = "准备导出…";
    public const string CompletedState = "导出完成";
    public const string CanceledState = "已取消";
    public const string FailedState = "导出失败";
    public const string EmptySpeedText = "";
    public const double MaxProgress = 100d;

    /// <summary>输出文件名：基础名 + .mp4。</summary>
    public static string FileName(string outputBaseName) => $"{outputBaseName}.mp4";

    /// <summary>导出模式文案。</summary>
    public static string ModeText(ClipExportMode mode) => mode switch
    {
        ClipExportMode.CleanOnly => "无弹幕",
        ClipExportMode.BurnedDanmaku => "弹幕烧录",
        _ => "双版"
    };

    /// <summary>时间区间文案：入点 - 出点（秒级时间码）。</summary>
    public static string RangeText(TimeSpan startTime, TimeSpan endTime)
        => $"{ClipListDisplayLogic.FormatTime(startTime)} - {ClipListDisplayLogic.FormatTime(endTime)}";

    /// <summary>速度文本：有倍率时保留一位小数，否则为空串。</summary>
    public static string SpeedText(double? speedMultiplier)
        => speedMultiplier is { } speed ? $"{speed:0.0}x" : EmptySpeedText;

    /// <summary>进度百分比钳制到 [0, 100]。</summary>
    public static double ClampProgress(double percentage)
        => Math.Clamp(percentage, 0d, MaxProgress);

    /// <summary>导出输出文件拼接：取文件名并用顿号连接。</summary>
    public static string BuildOutputText(IEnumerable<string>? files)
        => files is null ? string.Empty : string.Join("、", files.Select(Path.GetFileName));
}
