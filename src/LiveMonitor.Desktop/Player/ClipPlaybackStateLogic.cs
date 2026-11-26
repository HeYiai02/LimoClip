namespace LiveMonitor.Desktop.Player;

/// <summary>mpv 结束文件事件的简化原因（与第三方类型解耦）。</summary>
internal enum ClipEndReason
{
    Stop,
    Quit,
    Error,
    Eof,
    Other
}

/// <summary>结束文件事件的处理动作。</summary>
internal enum ClipEndAction
{
    Ignore,
    FailWhileLoading,
    Fail,
    Complete
}

/// <summary>
/// 剪辑台预览播放器的状态机与守卫规则（纯逻辑，不依赖 mpv/Avalonia/计时器）。
/// 视图只负责把 mpv 事件换算为输入，并按这里的结果更新控件。
/// </summary>
internal static class ClipPlaybackStateLogic
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    public const string LoadingDetail = "正在加载本地视频…";
    public const string PreparingDetail = "正在准备解码…";
    public const string CancelledDetail = "已取消加载";

    public static bool IsLoadedState(ClipPlayerState state) =>
        state is ClipPlayerState.Ready
            or ClipPlayerState.Playing
            or ClipPlayerState.Paused
            or ClipPlayerState.Ended;

    /// <summary>播放结束后再次点播放，需要先回到开头重播。</summary>
    public static bool ShouldRestartBeforePlay(ClipPlayerState state) =>
        state == ClipPlayerState.Ended;

    /// <summary>只有播放中才能暂停。</summary>
    public static bool CanPause(ClipPlayerState state) => state == ClipPlayerState.Playing;

    /// <summary>在结束态拖拽进度条后，状态回到暂停而不是继续播放。</summary>
    public static bool ShouldReturnToPausedAfterSeek(ClipPlayerState state) =>
        state == ClipPlayerState.Ended;

    /// <summary>收尾只允许在播放中/暂停中触发一次。</summary>
    public static bool CanRaiseCompletion(ClipPlayerState state, bool completionRaised) =>
        !completionRaised && state is ClipPlayerState.Playing or ClipPlayerState.Paused;

    /// <summary>状态未变化时不再通知；Failed 允许重复上报用于刷新失败详情。</summary>
    public static bool ShouldRaiseStateChanged(ClipPlayerState current, ClipPlayerState next) =>
        current != next || next == ClipPlayerState.Failed;

    /// <summary>播放完成后的播放位置：已知时长时停在末尾，否则保持当前位置。</summary>
    public static TimeSpan CompletionPosition(TimeSpan duration, TimeSpan position) =>
        duration > TimeSpan.Zero ? duration : position;

    public static double ClampSpeed(double speed) => Math.Clamp(speed, MinSpeed, MaxSpeed);

    public static string BuildMissingFileMessage(string filePath) => $"找不到视频文件：{filePath}";

    public static string BuildPlaybackFailedMessage(long error) => $"本地视频播放失败 (code {error})";

    /// <summary>加载中收到起播事件时，把加载提示细化为「正在准备解码」。</summary>
    public static bool ShouldShowPreparingDetail(ClipPlayerState state) =>
        state == ClipPlayerState.Loading;

    /// <summary>
    /// 结束文件事件的处理分类：Stop/Quit 由主动停止引起、忽略；
    /// Error 在加载阶段用于唤醒等待的加载任务，其余阶段标记失败；EOF 在已加载时触发收尾。
    /// </summary>
    public static ClipEndAction ClassifyEnd(ClipEndReason reason, bool isLoaded, bool isLoading) =>
        reason switch
        {
            ClipEndReason.Stop or ClipEndReason.Quit => ClipEndAction.Ignore,
            ClipEndReason.Error when isLoading => ClipEndAction.FailWhileLoading,
            ClipEndReason.Error => ClipEndAction.Fail,
            ClipEndReason.Eof when isLoaded => ClipEndAction.Complete,
            _ => ClipEndAction.Ignore
        };
}
