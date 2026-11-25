using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>剪辑台左侧「切换录像场次」下拉项：封装一条录制会话历史。</summary>
public sealed class ClipSessionOption
{
    public ClipSessionOption(RecordingSession session, LiveRecordingInfo? live = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        LiveInfo = live;
    }

    public RecordingSession Session { get; }

    /// <summary>录制中的实时信息；非空时表示这场录制仍在进行（边录边剪）。</summary>
    public LiveRecordingInfo? LiveInfo { get; }

    public bool IsLive => LiveInfo is not null;

    public string DisplayText => IsLive
        ? $"🔴 录制中 · {RoomTitle} (已录 {RecordingUiFormat.FormatDuration(LiveInfo!.RecordedDuration.TotalSeconds)})"
        : $"{StartedText} · {RoomTitle}";

    public string DetailText => IsLive
        ? $"已录 {RecordingUiFormat.FormatDuration(LiveInfo!.RecordedDuration.TotalSeconds)} · 弹幕 {LiveInfo.DanmakuCount} 条 · 高能 {LiveInfo.HighlightCount} 个"
        : $"{RecordingUiFormat.FormatDuration(Session.DurationSeconds)} · 弹幕 {Session.DanmakuCount} 条 · 高能 {Session.HighlightCount} 个";

    public string StartedText => RecordingUiFormat.FormatStartedAt(Session.StartedAt);

    public string RoomTitle => string.IsNullOrWhiteSpace(Session.RoomDisplayName)
        ? $"房间 {Session.RoomId}"
        : Session.RoomDisplayName;

    public bool HasVideo => IsLive
        ? !string.IsNullOrWhiteSpace(LiveInfo!.PartFilePath) && File.Exists(LiveInfo.PartFilePath)
        : !string.IsNullOrWhiteSpace(Session.VideoFilePath) && File.Exists(Session.VideoFilePath);
}
