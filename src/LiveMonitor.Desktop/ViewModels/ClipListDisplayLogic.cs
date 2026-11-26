namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台列表行（场次下拉、高能推荐）共用的展示文本规则：
/// 场次标题/摘要、高能速率与提示、语音转写按钮与摘要截断、秒级时间码。
/// 纯函数不依赖 Avalonia/播放器/数据库，便于单测覆盖。
/// </summary>
internal static class ClipListDisplayLogic
{
    public const int AsrSummaryMaxLength = 60;
    public const string AsrEmptySummary = "（未识别到文本）";
    public const string TranscribingText = "转写中…";
    public const string TranscribeText = "转写";
    public const string LiveRecordingPrefix = "🔴 录制中";

    private const string RoomFallbackFormat = "房间 {0}";

    /// <summary>秒级时间码；超过 1 小时用 h:mm:ss，否则 mm:ss，负值按 0。</summary>
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    /// <summary>场次标题：展示名为空时回退到「房间 {房间 Id}」。</summary>
    public static string RoomTitle(string? roomDisplayName, string? roomId)
        => string.IsNullOrWhiteSpace(roomDisplayName)
            ? string.Format(RoomFallbackFormat, roomId)
            : roomDisplayName;

    /// <summary>场次下拉主文本：录制中带 🔴 前缀与已录时长，否则为开始时间 + 标题。</summary>
    public static string BuildSessionDisplayText(bool isLive, string startedText, string roomTitle, double liveRecordedSeconds)
        => isLive
            ? $"{LiveRecordingPrefix} · {roomTitle} (已录 {RecordingUiFormat.FormatDuration(liveRecordedSeconds)})"
            : $"{startedText} · {roomTitle}";

    /// <summary>场次下拉副文本：录制中带「已录」前缀；时长 · 弹幕 N 条 · 高能 M 个。</summary>
    public static string BuildSessionDetailText(bool isLive, double durationSeconds, int danmakuCount, int highlightCount)
        => $"{(isLive ? "已录 " : string.Empty)}{RecordingUiFormat.FormatDuration(durationSeconds)} · 弹幕 {danmakuCount} 条 · 高能 {highlightCount} 个";

    /// <summary>高能速率文本，如「3.2/s」。</summary>
    public static string FormatRate(double ratePerSecond) => $"{ratePerSecond:0.0}/s";

    /// <summary>高能基线速率文本，如「基线 1.5/s」。</summary>
    public static string FormatBaselineRate(double ratePerSecond) => $"基线 {ratePerSecond:0.0}/s";

    /// <summary>高能卡片悬浮提示：时间 · 速率（基线）换行触发原因。</summary>
    public static string BuildHighlightToolTip(string timeText, string rateText, string baselineText, string reason)
        => $"{timeText} · {rateText}（{baselineText}）{Environment.NewLine}{reason}";

    /// <summary>高能卡片转写按钮文本。</summary>
    public static string TranscribeButtonText(bool isTranscribing)
        => isTranscribing ? TranscribingText : TranscribeText;

    /// <summary>转写摘要：完整文本 Trim 后截断到约 60 字符，空文本回退为占位提示。</summary>
    public static string BuildAsrSummary(string? fullText)
    {
        var text = fullText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return AsrEmptySummary;
        }

        return text.Length <= AsrSummaryMaxLength ? text : text[..AsrSummaryMaxLength] + "…";
    }

    /// <summary>转写摘要悬浮提示：完整文本（不截断），空文本回退为占位提示。</summary>
    public static string BuildAsrSummaryToolTip(string? fullText)
    {
        var text = fullText?.Trim();
        return string.IsNullOrEmpty(text) ? AsrEmptySummary : text;
    }
}
