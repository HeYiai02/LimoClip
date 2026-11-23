namespace LiveMonitor.Core.Entities;

/// <summary>
/// 单个直播间的录制偏好。只属于录制模块，不参与监控布局。
/// 主键即直播间主库 Id；删除直播间时随外键级联删除（录制历史不受影响）。
/// </summary>
public sealed class RecordingTarget
{
    /// <summary>直播间主库 Id（RecordingTargets 主键）。</summary>
    public Guid LiveRoomId { get; set; }

    /// <summary>开播后是否自动开始录制。</summary>
    public bool AutoRecordEnabled { get; set; }

    /// <summary>自定义保存目录；为空时使用全局默认目录。</summary>
    public string? CustomSaveDirectory { get; set; }

    /// <summary>以 UTC 保存；UI 负责转换成本地时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
