using LiveMonitor.Core.Entities;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>剪辑台左侧「切换录像场次」下拉项：封装一条录制会话历史。</summary>
public sealed class ClipSessionOption
{
    public ClipSessionOption(RecordingSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public RecordingSession Session { get; }

    public string DisplayText => $"{StartedText} · {RoomTitle}";

    public string DetailText =>
        $"{RecordingUiFormat.FormatDuration(Session.DurationSeconds)} · 弹幕 {Session.DanmakuCount} 条 · 高能 {Session.HighlightCount} 个";

    public string StartedText => RecordingUiFormat.FormatStartedAt(Session.StartedAt);

    public string RoomTitle => string.IsNullOrWhiteSpace(Session.RoomDisplayName)
        ? $"房间 {Session.RoomId}"
        : Session.RoomDisplayName;

    public bool HasVideo =>
        !string.IsNullOrWhiteSpace(Session.VideoFilePath) && File.Exists(Session.VideoFilePath);
}
