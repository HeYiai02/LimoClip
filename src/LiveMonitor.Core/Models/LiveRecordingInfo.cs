namespace LiveMonitor.Core.Models;

/// <summary>
/// 正在录制中的会话实时快照，供「边录边剪」在录制未停止时定位可读分片与安全剪辑边界。
/// </summary>
/// <remarks>
/// 录制引擎把每场录制写入分片文件（<c>{base}.partNNN.mkv</c>），会话记录里的
/// <c>VideoFilePath</c> 指向要等停止后才合并生成的最终文件，因此实时预览必须读取
/// <see cref="PartFilePath"/> 而不是 <c>VideoFilePath</c>。
/// </remarks>
/// <param name="SessionId">录制会话 Id。</param>
/// <param name="LiveRoomId">直播间主数据 Id（录制引擎按此 Id 索引活跃录制）。</param>
/// <param name="RoomDisplayNumber">直播间展示序号（0 表示未编号）。</param>
/// <param name="RoomDisplayName">直播间展示名（已含 <c>#序号</c> 前缀）。</param>
/// <param name="PartFilePath">当前正在写的分片绝对路径。</param>
/// <param name="PartNumber">当前分片序号（从 1 开始）。</param>
/// <param name="CompletedDuration">当前分片之前的已完成分片累计时长（首分片为 0）。</param>
/// <param name="RecordedDuration">本会话已录制总时长（已完成分片累计 + 当前分片）。</param>
/// <param name="FileSizeBytes">本会话已写入总字节数。</param>
/// <param name="DanmakuCount">已落盘弹幕条数。</param>
/// <param name="HighlightCount">本会话已关联的高能点数。</param>
public sealed record LiveRecordingInfo(
    Guid SessionId,
    Guid LiveRoomId,
    int RoomDisplayNumber,
    string RoomDisplayName,
    string PartFilePath,
    int PartNumber,
    TimeSpan CompletedDuration,
    TimeSpan RecordedDuration,
    long FileSizeBytes,
    int DanmakuCount,
    int HighlightCount);
