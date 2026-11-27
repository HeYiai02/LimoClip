using LiveMonitor.Core.Entities;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 边录边剪：录制结束「切离线」编排需要的 UI / 数据副作用，由 <see cref="ClipStudioViewModel"/>
/// 的嵌套宿主实现。协调器只负责顺序、取消与错误映射，不直接触碰控件或仓储。
/// </summary>
internal interface ILiveSessionCompletionHost
{
    /// <summary>视图模型是否已释放；已释放时协调器应立即中止后续步骤。</summary>
    bool IsDisposed { get; }

    /// <summary>当前 live 场次 Id；与上报场次不一致时忽略结束通知。</summary>
    Guid? CurrentSessionId { get; }

    /// <summary>结束瞬间的播放位置，切离线后恢复。</summary>
    TimeSpan LastPosition { get; }

    /// <summary>结束瞬间的入点，切离线后恢复选区。</summary>
    TimeSpan LastInPoint { get; }

    /// <summary>结束瞬间的出点，切离线后恢复选区。</summary>
    TimeSpan LastOutPoint { get; }

    /// <summary>停止实时定时器并重置 live 会话状态（属性通知由视图模型负责）。</summary>
    void ExitLiveMode();

    /// <summary>按 Id 读取场次的最终落盘信息。</summary>
    Task<RecordingSession?> LoadSessionAsync(CancellationToken ct);

    /// <summary>以离线模式打开最终文件（含弹幕与场次上下文）。</summary>
    Task OpenOfflineAsync(string videoPath, string? danmakuPath, RecordingSession session, CancellationToken ct);

    /// <summary>刷新左侧场次列表，让该场次从「录制中」切换为历史项。</summary>
    Task RefreshSessionsAsync(CancellationToken ct);

    /// <summary>恢复结束前的选区（入点、出点）。</summary>
    void RestoreSelection(TimeSpan inPoint, TimeSpan outPoint);

    /// <summary>恢复结束前的播放位置。</summary>
    void SeekTo(TimeSpan position);

    /// <summary>时间轴刷新事件。</summary>
    void RaiseTimelineChanged();

    /// <summary>写入状态栏文案。</summary>
    void SetStatus(string text);
}
