namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 窗格（tile）状态与副标题文案的唯一来源。
/// 主窗口的直播状态同步、播放器遮罩与工作区播放管理器共用这里的常量，
/// 避免「直播中」/「播放失败」等字面量在多个文件里各写一遍后失配。
/// </summary>
internal static class RoomStatusText
{
    /// <summary>空窗格占位状态。</summary>
    public const string EmptyPane = "空窗格";

    /// <summary>空窗格的提示副标题。</summary>
    public const string EmptyPaneDetail = "从左侧双击直播间，或拖入此窗格";

    /// <summary>房间已绑定、尚未起播。</summary>
    public const string WaitingForStart = "等待起播";

    /// <summary>正在读取房间状态。</summary>
    public const string ReadingStatus = "正在读取房间状态…";

    /// <summary>主播正在直播。</summary>
    public const string Live = "直播中";

    /// <summary>主播未开播。</summary>
    public const string Offline = "未开播";

    /// <summary>起播或播放过程中失败。</summary>
    public const string PlaybackFailed = "播放失败";

    /// <summary>直播流中断，进入自动重连。</summary>
    public const string Reconnecting = "重连中";

    /// <summary>直播流中断时播放器/窗格的副标题。</summary>
    public const string AutoReconnectDetail = "直播流已中断，正在自动重连…";

    /// <summary>直播流被对端关闭时的错误信息。</summary>
    public const string StreamInterrupted = "直播流已中断";

    /// <summary>播放器已挂载但还没有绑定房间。</summary>
    public const string WaitingForRoom = "等待加入房间";

    /// <summary>暂停按钮提示（当前正在播放，点击暂停）。</summary>
    public const string PauseTip = "暂停播放";

    /// <summary>暂停按钮提示（当前已暂停，点击继续）。</summary>
    public const string ResumeTip = "继续播放";

    /// <summary>开始拉流、建立播放通道。</summary>
    public const string OpeningChannel = "正在建立播放通道…";

    /// <summary>起播等待超时。</summary>
    public const string StartTimeout = "mpv 起播超时";

    /// <summary>用户主动取消加载。</summary>
    public const string Canceled = "已取消";

    /// <summary>用户主动停止播放。</summary>
    public const string Stopped = "已停止";

    /// <summary>重连/重载时先显示的连接中提示。</summary>
    public const string Connecting = "正在连接…";

    /// <summary>mpv 开始读取文件后的缓冲提示。</summary>
    public const string Buffering = "正在缓冲…";

    /// <summary>mpv 拉流失败（附带 mpv 错误码）。</summary>
    public static string StreamStartFailed(int errorCode) => $"mpv 拉流失败 (code {errorCode})";

    /// <summary>主播未开播时按轮询间隔提示下次重试时间。</summary>
    public static string OfflineDetail(TimeSpan pollDelay)
        => $"主播未开播，{DescribeSeconds(pollDelay)} 秒后重试";

    /// <summary>连续重连失败后按复检间隔提示下次自动复检时间。</summary>
    public static string FailureRecheckDetail(TimeSpan recheckDelay)
        => $"直播流已中断，{DescribeSeconds(recheckDelay)} 秒后自动重试";

    private static int DescribeSeconds(TimeSpan delay)
        => Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
}
