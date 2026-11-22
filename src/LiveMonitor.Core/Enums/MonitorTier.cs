namespace LiveMonitor.Core.Enums;

/// <summary>
/// 窗口画质档位。V0.1 仅根据窗口高度、是否焦点、在播路数三档切换。
/// </summary>
public enum MonitorTier
{
    /// <summary>焦点窗格，优先原画。</summary>
    Focus,

    /// <summary>常规窗格，720P 或原画。</summary>
    Standard,

    /// <summary>小窗格或路数过多，480P + 15fps。</summary>
    Compact15Fps
}
