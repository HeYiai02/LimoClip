namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 剪辑台本地预览播放器的遮罩标题与副标题文案唯一来源，
/// 与直播窗格的 <see cref="RoomStatusText"/> 分开：本地剪辑预览的状态语义是「预览/文件」，
/// 直播窗格是「起播/开播」，避免两边文案混用。
/// </summary>
internal static class ClipPreviewStatusText
{
    /// <summary>尚未选择视频。</summary>
    public const string EmptyTitle = "本地剪辑预览";

    /// <summary>尚未选择视频时的提示。</summary>
    public const string EmptyDetail = "从左侧列表选择录像，或打开本地文件";

    /// <summary>正在加载视频。</summary>
    public const string LoadingTitle = "加载中";

    /// <summary>正在加载视频时的提示。</summary>
    public const string LoadingDetail = "正在读取视频…";

    /// <summary>加载或解码失败。</summary>
    public const string FailedTitle = "播放失败";

    /// <summary>加载或解码失败时的提示。</summary>
    public const string FailedDetail = "请检查文件是否完整";

    /// <summary>遮罩标题；Ready/Playing/Paused/Ended 不显示遮罩，返回空串。</summary>
    public static string Title(ClipPlayerState state) => state switch
    {
        ClipPlayerState.Empty => EmptyTitle,
        ClipPlayerState.Loading => LoadingTitle,
        ClipPlayerState.Failed => FailedTitle,
        _ => string.Empty,
    };

    /// <summary>遮罩副标题；未提供具体原因时按状态回退到固定提示。</summary>
    public static string Detail(ClipPlayerState state) => state switch
    {
        ClipPlayerState.Empty => EmptyDetail,
        ClipPlayerState.Loading => LoadingDetail,
        ClipPlayerState.Failed => FailedDetail,
        _ => string.Empty,
    };
}
