using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Models;

/// <summary>
/// 一次高能事件。由 IHighlightDetector 触发，DanmakuManager 负责落盘并广播给界面。
/// <see cref="Id"/> 用于事件面板/持久化后的稳定标识。
/// </summary>
public sealed record HighlightEvent(
    PlatformType Platform,
    string RoomId,
    DateTimeOffset TriggerTime,
    double CurrentRatePerSec,
    double BaselineRatePerSec,
    string TriggerReason)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
