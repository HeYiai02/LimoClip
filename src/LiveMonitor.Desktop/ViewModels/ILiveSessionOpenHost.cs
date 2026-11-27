using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 一次 live 会话加载的版本号与取消令牌：由视图模型在开始加载时创建，
/// 协调器用它判断后续异步步骤是否已被更新的加载取代（切换场次 / 退出剪辑台）。
/// </summary>
internal readonly record struct LiveSessionLoadHandle(
    int Version,
    CancellationToken Token,
    CancellationToken WaveformToken);

/// <summary>
/// 「打开 live 会话」编排的宿主回调。
/// 协调器负责顺序与判定（分段拒绝、分片存在性、探测兜底、陈旧加载中止、失败文案），
/// 视图模型负责具体副作用（字段赋值、进入 live 模式、高光/波形初始化、播放器事件与属性通知），
/// 从而可以在无 Avalonia 的单元测试里断言打开流程的调用顺序与失败路径。
/// </summary>
internal interface ILiveSessionOpenHost
{
    /// <summary>开始一次新的加载：递增版本号并重建取消令牌（取消上一次未完成的加载）。</summary>
    LiveSessionLoadHandle BeginLoad();

    /// <summary>当前加载是否仍是最新一次（切换场次后旧加载返回 false）。</summary>
    bool IsLoadCurrent(LiveSessionLoadHandle handle);

    void SetBusy(bool busy);

    void SetStatus(string text);

    /// <summary>写入当前场次/分片/弹幕路径等字段，并重置字幕面板与高能识别状态。</summary>
    void PrepareLiveSessionFields(RecordingSession session, string partPath, string? danmakuPath);

    void SetLiveVideoTitle(string title);

    Task<VideoMetadata?> ProbeVideoAsync(string path, CancellationToken ct);

    /// <summary>会话就绪：编码兜底、进入 live 模式、时长/副标题/选区、清空高光与波形、重置弹幕游标。</summary>
    void EnterLiveSession(
        RecordingSession session,
        LiveRecordingInfo liveInfo,
        string partPath,
        VideoMetadata? metadata);

    Task LoadHighlightsAsync(RecordingSession session, TimeSpan duration, CancellationToken ct);

    /// <summary>打开收尾：占位波形、输出文件名、时间轴/弹幕/播放器事件、状态与导出可用性。</summary>
    void CompleteLiveOpen(string partPath);
}
