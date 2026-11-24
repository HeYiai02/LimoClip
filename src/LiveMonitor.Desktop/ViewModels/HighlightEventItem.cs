using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 高能事件面板里的单条展示项：把 Core 的 <see cref="HighlightEvent"/> 转成
/// 可直接绑定的文本与平台配色，避免 XAML 里写转换器。
/// </summary>
public sealed class HighlightEventItem
{
    public HighlightEventItem(HighlightEvent highlightEvent, string? roomTitle = null)
    {
        Id = highlightEvent.Id;
        Platform = highlightEvent.Platform;
        RoomId = highlightEvent.RoomId;
        TriggerTime = highlightEvent.TriggerTime;
        CurrentRatePerSec = highlightEvent.CurrentRatePerSec;
        BaselineRatePerSec = highlightEvent.BaselineRatePerSec;
        TriggerReason = highlightEvent.TriggerReason;
        RoomTitle = string.IsNullOrWhiteSpace(roomTitle) ? $"房间 {highlightEvent.RoomId}" : roomTitle;

        var (badge, background, foreground) = ResolvePlatform(highlightEvent.Platform);
        PlatformBadge = badge;
        PlatformBadgeBackground = background;
        PlatformBadgeForeground = foreground;
    }

    public Guid Id { get; }

    public PlatformType Platform { get; }

    public string RoomId { get; }

    public DateTimeOffset TriggerTime { get; }

    public double CurrentRatePerSec { get; }

    public double BaselineRatePerSec { get; }

    public string TriggerReason { get; }

    public string RoomTitle { get; }

    public string PlatformBadge { get; }

    public string PlatformBadgeBackground { get; }

    public string PlatformBadgeForeground { get; }

    /// <summary>本地时间 HH:mm:ss，面板里只展示时/分/秒。</summary>
    public string TimeText => TriggerTime.ToLocalTime().ToString("HH:mm:ss");

    public string RateText => $"短窗 {CurrentRatePerSec:F1} 条/秒 · 基线 {BaselineRatePerSec:F1} 条/秒";

    private static (string Badge, string Background, string Foreground) ResolvePlatform(PlatformType platform) => platform switch
    {
        PlatformType.Bilibili => ("B站", "#3A1B26", "#FB7299"),
        PlatformType.Douyin => ("抖音", "#1B2A2E", "#22D3EE"),
        PlatformType.Douyu => ("斗鱼", "#2E2415", "#FB923C"),
        PlatformType.Huya => ("虎牙", "#31230F", "#FACC15"),
        PlatformType.Twitch => ("Twitch", "#2A1B3D", "#C4A7FF"),
        PlatformType.YouTube => ("YouTube", "#3A1B1B", "#F87171"),
        PlatformType.CustomStream => ("清流", "#0B2933", "#22D3EE"),
        _ => ("其他", "#23262E", "#94A3B8")
    };
}
