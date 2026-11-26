using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 弹幕订阅与开关规则的纯逻辑：设置项布尔值解析（缺省开启）、订阅资格判定、
/// 已有订阅保持条件。让 <c>MainWindowViewModel</c> 只负责遍历与副作用。
/// </summary>
internal static class DanmakuSubscriptionLogic
{
    /// <summary>
    /// 设置页的布尔开关解析：未保存/空白视为开启，只有显式保存 <c>false</c>（忽略大小写与首尾空白）才关闭。
    /// 旧版本对弹幕总闸、贴纸、表情图片三个设置均使用该规则（表情图片默认开启）。
    /// </summary>
    public static bool IsEnabledSetting(string? raw)
        => string.IsNullOrWhiteSpace(raw)
           || !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>只有带房间、非自定义清流且房间号非空的窗格才订阅弹幕。</summary>
    public static bool IsSubscribeEligible(bool hasRoom, PlatformType platform, string? roomId)
        => hasRoom
           && platform != PlatformType.CustomStream
           && !string.IsNullOrWhiteSpace(roomId);

    /// <summary>
    /// 已有订阅的保持条件：窗格仍在工作区、平台未变、房间号未变；
    /// 否则需要释放旧句柄后重新订阅。
    /// </summary>
    public static bool ShouldKeepSubscription(
        bool stillActive,
        PlatformType platform,
        string? roomId,
        PlatformType existingPlatform,
        string? existingRoomId)
        => stillActive
           && platform == existingPlatform
           && string.Equals(roomId, existingRoomId, StringComparison.Ordinal);
}
