namespace LiveMonitor.Core.Models;

/// <summary>切片导出模式。</summary>
public enum ClipExportMode
{
    /// <summary>仅无弹幕版（<c>-c copy</c> 秒级无损）。</summary>
    CleanOnly = 0,

    /// <summary>仅有弹幕版（ASS 烧录 + 硬件编码）。</summary>
    BurnedDanmaku = 1,

    /// <summary>同时导出两版，文件名自动追加 <c>_clean</c> / <c>_danmaku</c> 后缀。</summary>
    Both = 2
}

/// <summary>烧录弹幕的 ASS 样式参数。</summary>
/// <param name="PlayResX">ASS 画布宽度。</param>
/// <param name="PlayResY">ASS 画布高度。</param>
/// <param name="FontName">字体名。</param>
/// <param name="FontSize">字号。</param>
/// <param name="ScrollDurationSeconds">单条弹幕滚动穿过屏幕的时长。</param>
/// <param name="DisplayAreaRatio">弹幕只占画面顶部比例（0.25~0.95），避免遮挡底部。</param>
/// <param name="OpacityPercent">不透明度百分比（100 = 完全不透明）。</param>
/// <param name="IncludeStickersAsText">大表情是否以文本形式烧录。</param>
public sealed record DanmakuAssStyle(
    int PlayResX = 1920,
    int PlayResY = 1080,
    string FontName = "Microsoft YaHei",
    int FontSize = 42,
    double ScrollDurationSeconds = 10.0,
    double DisplayAreaRatio = 0.65,
    int OpacityPercent = 85,
    bool IncludeStickersAsText = true);

/// <summary>一次切片导出请求。</summary>
/// <param name="InputVideoPath">源视频（.mkv / .mp4）。</param>
/// <param name="CompanionDanmakuPath">伴随弹幕 .jsonl；无弹幕版导出可为 null。</param>
/// <param name="OutputDirectory">输出目录。</param>
/// <param name="OutputBaseName">输出文件名（不含扩展名、不含模式后缀）。</param>
/// <param name="StartTime">入点（相对源视频起点）。</param>
/// <param name="EndTime">出点，必须大于入点。</param>
/// <param name="Mode">导出模式。</param>
/// <param name="AssStyle">烧录弹幕样式；null 时使用默认值。</param>
public sealed record ClipExportRequest(
    string InputVideoPath,
    string? CompanionDanmakuPath,
    string OutputDirectory,
    string OutputBaseName,
    TimeSpan StartTime,
    TimeSpan EndTime,
    ClipExportMode Mode,
    DanmakuAssStyle? AssStyle = null);
