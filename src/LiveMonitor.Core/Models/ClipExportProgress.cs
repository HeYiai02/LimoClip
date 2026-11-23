namespace LiveMonitor.Core.Models;

/// <summary>导出过程中的阶段进度。</summary>
/// <param name="StageLabel">阶段文案，如「正在无损导出无弹幕版...」。</param>
/// <param name="Percentage">0 ~ 100。</param>
/// <param name="SpeedMultiplier">ffmpeg 报告的实时速度倍率（如 4.2 表示 4.2x）。</param>
public sealed record ClipExportProgress(
    string StageLabel,
    double Percentage,
    double? SpeedMultiplier);
