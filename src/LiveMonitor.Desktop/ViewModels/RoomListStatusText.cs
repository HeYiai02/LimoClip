using LiveMonitor.Desktop.Player;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 左侧主库列表行的开播状态展示映射：状态提示与圆点色唯一来源。
/// 文案与窗格状态共用 <see cref="RoomStatusText"/> 的「直播中/未开播」，
/// 这里只补充列表特有的「状态未知」提示与圆点色。
/// </summary>
internal static class RoomListStatusText
{
    /// <summary>直播中圆点色。</summary>
    public const string LiveBrush = "#3DDC97";

    /// <summary>未开播圆点色。</summary>
    public const string OfflineBrush = "#5B6472";

    /// <summary>尚未检测到开播状态的圆点色。</summary>
    public const string UnknownBrush = "#3A414D";

    /// <summary>尚未检测到开播状态时的提示。</summary>
    public const string UnknownTip = "在线状态未知（双击放入工作区后检测）";

    /// <summary>状态提示：直播中 / 未开播 / 状态未知（null）。</summary>
    public static string Tip(bool? isLive) => isLive switch
    {
        true => RoomStatusText.Live,
        false => RoomStatusText.Offline,
        null => UnknownTip,
    };

    /// <summary>状态圆点颜色：直播中 / 未开播 / 状态未知（null）。</summary>
    public static string Brush(bool? isLive) => isLive switch
    {
        true => LiveBrush,
        false => OfflineBrush,
        null => UnknownBrush,
    };
}
