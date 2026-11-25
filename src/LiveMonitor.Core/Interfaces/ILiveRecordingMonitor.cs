using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 录制引擎的实时查询入口：「边录边剪」用它定位正在写的分片、跟随录制进度，
/// 并在录制结束（完成或失败）时收到通知切回普通离线剪辑。
/// </summary>
public interface ILiveRecordingMonitor
{
    /// <summary>返回当前所有录制中会话的实时快照；无活跃录制时返回空列表。</summary>
    IReadOnlyList<LiveRecordingInfo> GetLiveRecordings();

    /// <summary>按会话 Id 查询正在录制的实时快照；会话不存在或已结束返回 false。</summary>
    bool TryGetLiveRecording(Guid sessionId, out LiveRecordingInfo info);

    /// <summary>会话结束（成功或失败）事件；录制引擎在收尾、分片合并完成后触发。</summary>
    event Action<RecordingSession>? SessionCompleted;
}
