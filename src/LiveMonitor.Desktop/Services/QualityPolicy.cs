using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 画质档位选择策略：大窗优先原画，中窗 720P，小窗/多路 480P + 15fps。
/// 纯函数，方便单测和后续接入焦点槽。
/// </summary>
public static class QualityPolicy
{
    public static bool ShouldUseCompact(double height, int activeRoomCount)
        => activeRoomCount >= 12 || (height > 0 && height < 360);

    public static StreamQuality? Choose(
        RoomStatus status,
        double height,
        int activeRoomCount,
        bool isFocusSlot = false)
    {
        var ordered = status.AvailableQualities
            .Where(quality => quality.Height > 0)
            .OrderByDescending(quality => quality.Height)
            .ThenByDescending(quality => quality.Fps)
            .ToList();

        if (ordered.Count == 0)
        {
            return status.AvailableQualities.FirstOrDefault();
        }

        // 窗格还没完成布局时按 720P 处理，避免启动瞬间拉 1080P 再降档。
        var effectiveHeight = height > 0 ? height : 720;
        if (isFocusSlot || (activeRoomCount < 12 && effectiveHeight > 720))
        {
            return ordered[0];
        }

        var targetHeight = ShouldUseCompact(effectiveHeight, activeRoomCount) ? 480 : 720;
        return ordered.FirstOrDefault(quality => quality.Height <= targetHeight) ?? ordered[^1];
    }
}
