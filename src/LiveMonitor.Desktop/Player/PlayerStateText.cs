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
}
