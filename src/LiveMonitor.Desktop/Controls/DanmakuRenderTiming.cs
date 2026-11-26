namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕滚动共享帧循环的纯节拍规则：帧间隔换算与停滞看门狗判定。
/// 与 Avalonia 的帧回调/计时器解耦，便于直接单测。
/// </summary>
internal static class DanmakuRenderTiming
{
    /// <summary>单帧最大推进秒数：切后台/卡顿后回前台时避免弹幕瞬移。</summary>
    public const double MaxFrameDelta = 0.1;

    /// <summary>单帧最小推进秒数：同一毫秒内多次回调时保证仍可前进。</summary>
    public const double MinFrameDelta = 0.001;

    /// <summary>首帧没有上一帧时间戳时按 60fps 估算。</summary>
    public const double FirstFrameDelta = 1.0 / 60.0;

    /// <summary>超过该时长没有帧回调即视为帧循环停滞，需要重新排队。</summary>
    public const long FrameStallThresholdMilliseconds = 1200;

    /// <summary>
    /// 根据上一帧时间戳与当前帧时间戳计算本帧推进秒数。
    /// 首帧返回 <see cref="FirstFrameDelta"/>；其它情况钳制在
    /// [<see cref="MinFrameDelta"/>, <see cref="MaxFrameDelta"/>]。
    /// </summary>
    public static double ResolveFrameDelta(TimeSpan? lastTimestamp, TimeSpan currentTimestamp)
    {
        if (lastTimestamp is not { } last)
        {
            return FirstFrameDelta;
        }

        var seconds = (currentTimestamp - last).TotalSeconds;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return FirstFrameDelta;
        }

        return Math.Clamp(seconds, MinFrameDelta, MaxFrameDelta);
    }

    /// <summary>
    /// 判断帧循环是否停滞：距最后一次帧回调已超过
    /// <see cref="FrameStallThresholdMilliseconds"/>。
    /// </summary>
    public static bool IsFrameStalled(long lastFrameTickMilliseconds, long nowTickMilliseconds)
    {
        if (lastFrameTickMilliseconds <= 0)
        {
            return false;
        }

        return nowTickMilliseconds - lastFrameTickMilliseconds >= FrameStallThresholdMilliseconds;
    }
}
