namespace LiveMonitor.Core.Models;

/// <summary>录制中的实时进度快照，用于录制中心卡片的指标刷新。</summary>
/// <param name="SessionId">录制会话 Id。</param>
/// <param name="Duration">已录制时长。</param>
/// <param name="FileSizeBytes">已写入文件的字节数。</param>
/// <param name="BitrateKbps">ffmpeg 报告的实时码率（kbps）。</param>
/// <param name="DanmakuCount">已落盘弹幕条数。</param>
/// <param name="HighlightCount">本次会话已关联的高能点数。</param>
public sealed record RecordingProgress(
    Guid SessionId,
    TimeSpan Duration,
    long FileSizeBytes,
    double BitrateKbps,
    int DanmakuCount,
    int HighlightCount);
