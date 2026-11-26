namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台语音识别面板的状态与错误文案模板（V0.7.5 集中）。
/// </summary>
internal static class AsrStatusText
{
    public const string SelectionInvalidError = "请先在时间轴上选择有效的入出点区间。";
    public const string SelectionInvalid = "选区无效";
    public const string HighlightTooShortError = "高能片段过短，无法转写。";
    public const string HighlightTooShort = "片段过短，无法转写";
    public const string Canceling = "正在取消…";
    public const string Copied = "已复制到剪贴板";
    public const string Idle = "尚未识别";
    public const string RunningSelection = "正在识别当前选区…";
    public const string RunningHighlight = "正在转写高能片段…";
    public const string Canceled = "已取消";
    public const string Failed = "识别失败";
    public const string NoVideo = "请先打开本地视频";

    public static string CopyFailed(string message) => $"复制识别文本失败：{message}";
}
