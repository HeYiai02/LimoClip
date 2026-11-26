namespace LiveMonitor.Danmaku;

/// <summary>
/// 弹幕连接断线重连的退避梯度与稳定连接重置规则（纯逻辑，便于单测）。
/// </summary>
internal static class DanmakuReconnectPolicy
{
    /// <summary>断线重连退避序列：3s → 10s → 30s → 60s，之后固定 60s。</summary>
    public static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    /// <summary>一次连接持续超过该时长后，断线时把退避重置回 3 秒。</summary>
    public static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(30);

    /// <summary>取第 <paramref name="attemptIndex"/> 次重连的等待时长；越界夹到首尾档位。</summary>
    public static TimeSpan DelayFor(int attemptIndex)
        => BackoffSchedule[Math.Clamp(attemptIndex, 0, BackoffSchedule.Length - 1)];

    /// <summary>推进到下一次重连的退避档位，末档后固定为最后一档（60 秒）。</summary>
    public static int NextAttemptIndex(int attemptIndex)
        => Math.Min(Math.Max(attemptIndex, 0) + 1, BackoffSchedule.Length - 1);

    /// <summary>
    /// 连接稳定超过阈值时重置退避档位；未达阈值（或本次未连上）保持当前档位。
    /// </summary>
    public static int ResetIndexIfStable(int attemptIndex, TimeSpan connectedDuration)
        => connectedDuration >= StableConnectionThreshold ? 0 : Math.Max(attemptIndex, 0);
}
