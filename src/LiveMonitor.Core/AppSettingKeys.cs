namespace LiveMonitor.Core;

/// <summary>
/// AppSettings 的已知键名，避免 UI/服务层散落魔法字符串。
/// </summary>
public static class AppSettingKeys
{
    public const string BlockUnicycleEnabled = "BlockUnicycleEnabled";
    public const string DanmakuPreviewEnabled = "DanmakuPreviewEnabled";

    // V0.2 高能检测参数（由设置页持久化，DanmakuManager 启动时读取并热更新）
    public const string HighlightShortWindowSeconds = "HighlightShortWindowSeconds";
    public const string HighlightBaselineWindowSeconds = "HighlightBaselineWindowSeconds";
    public const string HighlightDensityMultiplier = "HighlightDensityMultiplier";
    public const string HighlightMinShortWindowCount = "HighlightMinShortWindowCount";
    public const string HighlightCooldownSeconds = "HighlightCooldownSeconds";
    public const string HighlightBoostKeywords = "HighlightBoostKeywords";
}
