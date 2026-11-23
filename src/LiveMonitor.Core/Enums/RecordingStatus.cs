namespace LiveMonitor.Core.Enums;

/// <summary>
/// 一次录制会话的生命周期状态。持久化时存整数，枚举值与 V0.3 设计文档保持一致。
/// </summary>
public enum RecordingStatus
{
    /// <summary>会话已创建，ffmpeg 尚未确认起流（尚未锚定首帧）。</summary>
    Starting = 0,

    /// <summary>正在录制。</summary>
    Recording = 1,

    /// <summary>已发出优雅停止请求，等待 ffmpeg 收尾写完文件索引。</summary>
    Stopping = 2,

    /// <summary>正常结束，视频文件完整。</summary>
    Completed = 3,

    /// <summary>异常结束（启动失败、崩溃恢复、断流放弃等），文件可能不完整。</summary>
    Failed = 4,
}
