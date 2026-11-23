namespace LiveMonitor.Core;

/// <summary>
/// AppSettings 的已知键名，避免 UI/服务层散落魔法字符串。
/// </summary>
public static class AppSettingKeys
{
    public const string BlockUnicycleEnabled = "BlockUnicycleEnabled";
    public const string DanmakuPreviewEnabled = "DanmakuPreviewEnabled";

    /// <summary>
    /// 是否在监控画面渲染纯大表情包弹幕（V0.2 默认只显示 [表情名] 文本；关闭后仅统计与高能检测，不渲染）。
    /// </summary>
    public const string DanmakuStickerVisible = "DanmakuStickerVisible";

    /// <summary>
    /// 是否把表情渲染成静态图片（V0.2 可选，默认关闭；关闭时显示 [doge]/[表情名] 文本）。
    /// </summary>
    public const string DanmakuEmoteImagesVisible = "DanmakuEmoteImagesVisible";

    // V0.2 高能检测参数（由设置页持久化，DanmakuManager 启动时读取并热更新）
    public const string HighlightShortWindowSeconds = "HighlightShortWindowSeconds";
    public const string HighlightBaselineWindowSeconds = "HighlightBaselineWindowSeconds";
    public const string HighlightDensityMultiplier = "HighlightDensityMultiplier";
    public const string HighlightMinShortWindowCount = "HighlightMinShortWindowCount";
    public const string HighlightCooldownSeconds = "HighlightCooldownSeconds";
    public const string HighlightBoostKeywords = "HighlightBoostKeywords";
}
