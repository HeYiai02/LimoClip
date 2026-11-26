using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 自动降档守护：按丢帧率的多轮巡检决定是否降档，并选择下一档可用画质。
/// 纯函数，方便单测；巡检节奏与切档副作用仍留在 <see cref="WorkspacePlayerManager"/>。
/// </summary>
public static class DropGuardPolicy
{
    /// <summary>单轮丢帧率超过该阈值记为一次“坏周期”。</summary>
    public const double BadDropRateThreshold = 0.05;

    /// <summary>连续坏周期达到该次数后触发降档。</summary>
    public const int BadCyclesBeforeDowngrade = 3;

    /// <summary>播放器未上报有效帧率时用于估算丢帧率的兜底值。</summary>
    public const double DefaultFramesPerSecond = 30;

    public static long TotalDroppedFrames(PlayerHealthMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return metrics.DecoderDroppedFrames + metrics.DroppedFrames;
    }

    /// <summary>帧率缺失或异常（&lt;= 1）时按兜底帧率处理，避免丢帧率被无限放大。</summary>
    public static double ResolveFramesPerSecond(double framesPerSecond)
        => framesPerSecond > 1 ? framesPerSecond : DefaultFramesPerSecond;

    /// <summary>按采样区间计算丢帧率；时间/帧率非法或丢帧为负时返回 0。</summary>
    public static double ResolveDropRate(long droppedDelta, double framesPerSecond, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 ||
            !double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
        {
            return 0;
        }

        var delta = Math.Max(0, droppedDelta);
        return delta / (framesPerSecond * elapsedSeconds);
    }

    public static bool IsBadCycle(double dropRate) => dropRate > BadDropRateThreshold;

    public static int NextBadCycles(bool isBadCycle, int previousBadCycles)
        => isBadCycle ? previousBadCycles + 1 : 0;

    public static bool ShouldDowngrade(int badCycles) => badCycles >= BadCyclesBeforeDowngrade;

    /// <summary>在当前档位之下选最高可用档（同高度优先高帧率）；没有更低档时返回 null。</summary>
    public static StreamQuality? ChooseLowerQuality(StreamQuality? current, IReadOnlyList<StreamQuality> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (current is null)
        {
            return null;
        }

        return available
            .Where(quality => quality.Height < current.Height)
            .OrderByDescending(quality => quality.Height)
            .ThenByDescending(quality => quality.Fps)
            .FirstOrDefault();
    }
}
