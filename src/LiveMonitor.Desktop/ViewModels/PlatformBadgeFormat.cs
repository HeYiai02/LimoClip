using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 平台徽章与名称的唯一来源：短徽章文字、徽章底色、徽章前景色与长名称。
/// 房间列表、工作区窗格、录制中心此前各写一份映射，改平台文案时容易漏改，统一到这里。
/// </summary>
internal static class PlatformBadgeFormat
{
    /// <summary>短徽章文字，如「B站」「自定义清流」缩写为「清流」。</summary>
    public static string Text(PlatformType platform) => platform switch
    {
        PlatformType.Bilibili => "B站",
        PlatformType.Douyin => "抖音",
        PlatformType.Douyu => "斗鱼",
        PlatformType.Huya => "虎牙",
        PlatformType.Twitch => "Twitch",
        PlatformType.YouTube => "YouTube",
        PlatformType.CustomStream => "清流",
        _ => "—"
    };

    /// <summary>徽章底色。</summary>
    public static string Background(PlatformType platform) => platform switch
    {
        PlatformType.Bilibili => "#FB7299",
        PlatformType.Twitch => "#9146FF",
        PlatformType.Douyin => "#FE2C55",
        PlatformType.Douyu => "#FF6A00",
        PlatformType.Huya => "#FFB300",
        PlatformType.YouTube => "#FF0033",
        PlatformType.CustomStream => "#0EA5E9",
        _ => "#2A3140"
    };

    /// <summary>徽章前景色（仅虎牙亮黄底色需要深色文字）。</summary>
    public static string Foreground(PlatformType platform) => platform switch
    {
        PlatformType.Huya => "#1A1A1A",
        _ => "#FFFFFF"
    };

    /// <summary>完整平台名称，用于录制中心等需要「哔哩哔哩」「自定义清流」的场景。</summary>
    public static string DisplayName(PlatformType platform) => platform switch
    {
        PlatformType.Bilibili => "哔哩哔哩",
        PlatformType.Douyin => "抖音",
        PlatformType.Douyu => "斗鱼",
        PlatformType.Huya => "虎牙",
        PlatformType.Twitch => "Twitch",
        PlatformType.YouTube => "YouTube",
        PlatformType.CustomStream => "自定义清流",
        _ => "其他平台"
    };
}
