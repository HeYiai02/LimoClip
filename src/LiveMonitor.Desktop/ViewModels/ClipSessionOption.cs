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

    public string DisplayText => ClipListDisplayLogic.BuildSessionDisplayText(
        IsLive,
        StartedText,
        RoomTitle,
        IsLive ? LiveInfo!.RecordedDuration.TotalSeconds : Session.DurationSeconds);

    public string DetailText => ClipListDisplayLogic.BuildSessionDetailText(
        IsLive,
        IsLive ? LiveInfo!.RecordedDuration.TotalSeconds : Session.DurationSeconds,
        IsLive ? LiveInfo!.DanmakuCount : Session.DanmakuCount,
        IsLive ? LiveInfo!.HighlightCount : Session.HighlightCount);

    public string StartedText => RecordingUiFormat.FormatStartedAt(Session.StartedAt);

    public string RoomTitle => ClipListDisplayLogic.RoomTitle(Session.RoomDisplayName, Session.RoomId);

    public bool HasVideo => IsLive
        ? !string.IsNullOrWhiteSpace(LiveInfo!.PartFilePath) && File.Exists(LiveInfo.PartFilePath)
        : !string.IsNullOrWhiteSpace(Session.VideoFilePath) && File.Exists(Session.VideoFilePath);
}
