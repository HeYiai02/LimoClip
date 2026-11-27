using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// <see cref="LiveSessionCoordinator"/> 对视图模型的显式回调边界：协调器只决定「按什么顺序做什么」，
/// 进度/弹幕/波形/高能等数据落点与 UI 通知仍由 <see cref="ClipStudioViewModel"/> 执行。
/// </summary>
internal interface ILiveSessionSyncHost
{
    /// <summary>视图模型已释放（同步循环应立即放弃后续操作）。</summary>
    bool IsDisposed { get; }

    /// <summary>是否允许 10 秒自动同步。</summary>
    bool IsAutoSyncEnabled { get; }

    /// <summary>当前打开的场次（live 模式的数据上下文）；null 表示未加载场次。</summary>
    RecordingSession? CurrentSession { get; }

    /// <summary>当前场次伴随弹幕路径；null 表示无弹幕文件。</summary>
    string? DanmakuPath { get; }

    /// <summary>已录制的实时时长。</summary>
    TimeSpan LiveRecordedDuration { get; }

    /// <summary>安全可剪截止时间。</summary>
    TimeSpan SafeClipDuration { get; }

    /// <summary>应用最新录制进度：更新 live 会话、总时长，并把播放头钳制到安全区之内。</summary>
    void ApplyProgress(LiveRecordingInfo latest);

    /// <summary>把增量弹幕追加到回放数据与游标并通知 UI。</summary>
    void ApplyDanmaku(IReadOnlyList<RecordedDanmakuItem> items);

    /// <summary>按当前已录时长重建波形数据。</summary>
    void ApplyWaveform();

    /// <summary>更新波形加载中状态。</summary>
    void SetWaveformLoading(bool loading);

    /// <summary>全量拉取最近高能记录并重建提示标记（原实现无增量接口）。</summary>
    Task ReloadHighlightsAsync(TimeSpan duration, CancellationToken ct);

    /// <summary>请求播放器重载尾部数据（仅手动同步使用）。</summary>
    void RequestPlayerReload();

    /// <summary>通知时间轴/选区数据变化。</summary>
    void RaiseTimelineChanged();

    /// <summary>写入状态栏文案。</summary>
    void SetStatus(string text);

    /// <summary>同步开始/结束通知（用于命令可用性与「同步中」指示）。</summary>
    void SetSyncing(bool syncing);

    /// <summary>录制进入后续分片时停止边录边剪并提示。</summary>
    void StopForSegmentedRecording(int partNumber);

    /// <summary>同步时发现录制已结束：切换为离线剪辑模式。</summary>
    Task HandleRecordingCompletedAsync();
}
