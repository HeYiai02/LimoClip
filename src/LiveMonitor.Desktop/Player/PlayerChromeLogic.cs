namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 播放器窗格 chrome（控制条/图标/弹幕层）的纯状态规则，便于单测。
/// </summary>
internal static class PlayerChromeLogic
{
    /// <summary>Segoe Fluent Icons：E74F = 静音图标；E767 = 音量图标。</summary>
    public const string MutedIconGlyph = "\uE74F";

    public const string UnmutedIconGlyph = "\uE767";

    public const int MinVolume = 0;

    public const int MaxVolume = 100;

    public const int CompactMaxConcurrent = 15;

    public const int NormalMaxConcurrent = 30;

    public static string MuteIcon(bool muted) => muted ? MutedIconGlyph : UnmutedIconGlyph;

    public static string MuteTip(bool muted) => muted ? "开声" : "静音";

    /// <summary>紧凑模式按钮显示当前档位文案（点击后切到另一档）。</summary>
    public static string CompactButtonText(bool compact) => compact ? "15fps" : "60fps";

    /// <summary>紧凑模式限制同屏弹幕并发数，降低渲染压力。</summary>
    public static int MaxConcurrentFor(bool compact) => compact ? CompactMaxConcurrent : NormalMaxConcurrent;

    /// <summary>弹幕层实际可见性 = 全局总闸 &amp;&amp; 当前窗格弹幕开关。</summary>
    public static bool DanmakuLayerVisible(bool masterEnabled, bool tileDanmakuVisible)
        => masterEnabled && tileDanmakuVisible;

    public static int ClampVolume(int volume) => Math.Clamp(volume, MinVolume, MaxVolume);
}
