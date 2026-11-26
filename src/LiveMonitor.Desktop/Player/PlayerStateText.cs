using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 播放器状态到窗格状态文案的统一映射。
/// 特别说明：<see cref="PlayerState.Offline"/> 只表示「主播未开播」，
/// 直播流意外中断使用 <see cref="PlayerState.Reconnecting"/>，不能混用。
/// </summary>
public static class PlayerStateText
{
    public static string Describe(PlayerState state) => state switch
    {
        PlayerState.Loading => "连接中",
        PlayerState.Playing => "直播中",
        PlayerState.Offline => "已停止",
        PlayerState.Reconnecting => "重连中",
        PlayerState.Failed => "播放失败",
        _ => state.ToString(),
    };

    /// <summary>
    /// 播放器状态遮罩的显示规则与默认文案（自定义 detail 由调用方覆盖）。
    /// </summary>
    public static PlayerOverlay Overlay(PlayerState state) => state switch
    {
        PlayerState.Loading => new PlayerOverlay(
            ShowOverlay: true,
            ShowLoadingBar: true,
            ShowRetry: false,
            Title: "加载中",
            Detail: "正在解析直播流…"),
        PlayerState.Offline => new PlayerOverlay(
            ShowOverlay: true,
            ShowLoadingBar: false,
            ShowRetry: false,
            Title: "未开播",
            Detail: "等待主播开播"),
        PlayerState.Reconnecting => new PlayerOverlay(
            ShowOverlay: true,
            ShowLoadingBar: true,
            ShowRetry: true,
            Title: "直播流中断",
            Detail: "正在自动重连…"),
        PlayerState.Failed => new PlayerOverlay(
            ShowOverlay: true,
            ShowLoadingBar: false,
            ShowRetry: true,
            Title: "播放失败",
            Detail: "可点击重新连接"),
        _ => new PlayerOverlay(
            ShowOverlay: state != PlayerState.Playing,
            ShowLoadingBar: false,
            ShowRetry: false,
            Title: string.Empty,
            Detail: string.Empty),
    };
}

/// <summary>
/// 播放器状态遮罩的展示模型：是否显示遮罩 / 加载条 / 重试按钮，以及默认标题与详情。
/// </summary>
public readonly record struct PlayerOverlay(
    bool ShowOverlay,
    bool ShowLoadingBar,
    bool ShowRetry,
    string Title,
    string Detail);
