namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 设置页「通用与弹幕」分区的状态提示文案（V0.7.5 集中）：
/// 读取/屏蔽词增删/高能参数保存与重置/Twitch 代理保存，以及四个弹幕开关的成功与失败提示。
/// </summary>
internal static class GeneralAndDanmakuSettingsStatusText
{
    public const string KeywordExists = "这个词已经在屏蔽词库中。";
    public const string KeywordAdded = "已添加屏蔽词。";
    public const string RegexKeywordAdded = "已添加正则屏蔽词。";
    public const string KeywordNotSelected = "请先选中一个屏蔽词。";
    public const string KeywordRemoved = "已删除屏蔽词。";
    public const string HighlightSaved = "高能参数已保存（对运行中的会话即时生效）。";
    public const string HighlightReset = "高能参数已恢复默认值。";
    public const string TwitchProxySavedBuiltin = "已保存：Twitch 仅使用内置原生播放列表（GQL + Usher）。";
    public const string TwitchProxySavedCustom = "已保存：Twitch 将优先使用自定义去广告代理。";

    public const string UnicycleEnabled = "独轮车自动屏蔽已开启。";
    public const string UnicycleDisabled = "独轮车自动屏蔽已关闭。";
    public const string UnicycleSaveFailedPrefix = "保存开关失败";

    public const string DanmakuPreviewEnabled = "监控画面弹幕预览已开启。";
    public const string DanmakuPreviewDisabled = "监控画面弹幕预览已关闭。";
    public const string DanmakuPreviewSaveFailedPrefix = "保存弹幕预览开关失败";

    public const string StickerEnabled = "纯表情包会渲染为 [表情名] 文本。";
    public const string StickerDisabled = "纯表情包仅参与统计与高能检测，不再渲染。";
    public const string StickerSaveFailedPrefix = "保存表情包渲染开关失败";

    public const string EmoteImagesEnabled = "表情图片已开启（仅渲染静态帧，未加载成功的表情先显示 [表情名]）。";
    public const string EmoteImagesDisabled = "表情图片已关闭，表情按 [表情] 文本显示。";
    public const string EmoteImagesSaveFailedPrefix = "保存表情图片开关失败";

    public static string LoadFailed(string message) => $"读取设置失败：{message}";

    public static string AddKeywordFailed(string message) => $"添加失败：{message}";

    public static string RemoveKeywordFailed(string message) => $"删除失败：{message}";

    public static string HighlightSaveFailed(string message) => $"保存高能参数失败：{message}";

    public static string HighlightResetFailed(string message) => $"恢复默认值失败：{message}";

    public static string TwitchProxySaveFailed(string message) => $"保存 Twitch 代理失败：{message}";

    public static string ToggleSaveFailed(string failurePrefix, string message) => $"{failurePrefix}：{message}";

    /// <summary>新增屏蔽词成功文案：正则与普通词使用不同措辞。</summary>
    public static string KeywordAddedFor(bool isRegex) => isRegex ? RegexKeywordAdded : KeywordAdded;
}
