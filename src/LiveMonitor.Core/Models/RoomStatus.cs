using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Models;

/// <summary>
/// 房间当前状态。未开播时 AvailableQualities 为空。
/// </summary>
public sealed record RoomStatus(
    string RoomId,
    PlatformType Platform,
    string Title,
    string AnchorName,
    bool IsLive,
    IReadOnlyList<StreamQuality> AvailableQualities)
{
    public static RoomStatus Offline(string roomId, PlatformType platform, string title = "", string anchorName = "")
        => new(roomId, platform, title, anchorName, false, Array.Empty<StreamQuality>());
}
