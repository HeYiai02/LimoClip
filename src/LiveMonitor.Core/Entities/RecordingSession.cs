using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Entities;

/// <summary>
/// 一次完整录制会话（一对同名 .mkv + .jsonl）的历史记录。
/// <para>
/// 会话独立于直播间生命周期：删除直播间时 <see cref="LiveRoomId"/> 由外键置空，记录本身保留；
/// 重新添加同一 (Platform, RoomId) 的直播间后，可按平台房间号再次看到这些历史。
/// </para>
/// </summary>
public sealed class RecordingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>关联的直播间主库 Id；直播间被删除后为 null。</summary>
    public Guid? LiveRoomId { get; set; }

    public PlatformType Platform { get; set; }

    /// <summary>平台侧房间标识（直播间删除后仍保留，用于重新关联历史）。</summary>
    public string RoomId { get; set; } = string.Empty;

    /// <summary>开始录制时的展示名快照，如「#1 晚风游戏室」。</summary>
    public string RoomDisplayName { get; set; } = string.Empty;

    /// <summary>开播标题快照，可为空。</summary>
    public string StreamTitle { get; set; } = string.Empty;

    public RecordingStatus Status { get; set; } = RecordingStatus.Starting;

    /// <summary>视频文件绝对路径（.mkv）。</summary>
    public string VideoFilePath { get; set; } = string.Empty;

    /// <summary>弹幕伴随文件绝对路径（.jsonl）。</summary>
    public string DanmakuFilePath { get; set; } = string.Empty;

    /// <summary>录制起点（UTC，首帧锚定时间；未锚定前为会话创建时间）。</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>录制终点（UTC）；尚未结束时为 null。</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>录制时长（秒），停录后按文件实际时长回填。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>视频文件大小（字节），停录后回填。</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>本次录制期间成功写入 .jsonl 的弹幕条数。</summary>
    public int DanmakuCount { get; set; }

    /// <summary>本次录制期间触发并绑定到该会话的高能点数。</summary>
    public int HighlightCount { get; set; }

    /// <summary>失败原因；成功结束为 null。</summary>
    public string? ErrorMessage { get; set; }
}
