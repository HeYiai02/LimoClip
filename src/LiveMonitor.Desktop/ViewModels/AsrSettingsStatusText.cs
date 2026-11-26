namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 设置页「语音识别（ASR）」分区的展示文案（V0.7.5 集中）：
/// 语言下拉显示名、API Key 输入框 Watermark、测试按钮、状态栏与失败提示。
/// </summary>
internal static class AsrSettingsStatusText
{
    /// <summary>语言下拉显示名，与持久化 Id（auto/zh/en/ja/ko）按下标一一对应。</summary>
    public static readonly IReadOnlyList<string> LanguageNames = new[] { "自动检测", "中文", "英文", "日文", "韩文" };

    public const string Initial = "尚未读取语音识别设置。";
    public const string LoadedConfigured = "已读取语音识别设置。";
    public const string LoadedMissingApiKey = "尚未配置 API Key，请粘贴后点击「保存」。";
    public const string WatermarkSaved = "已保存，留空保持不变";
    public const string WatermarkEmpty = "粘贴 API Key";
    public const string TestButtonIdle = "测试连接";
    public const string TestButtonRunning = "正在测试…";
    public const string Testing = "正在测试连接…";
    public const string Saving = "正在保存语音识别设置…";
    public const string Saved = "语音识别设置已保存。";
    public const string CustomPresetHint = "自定义服务需要填写接口地址和模型（OpenAI 兼容）。";
    public const string InvalidCustomPreset = "自定义服务需要填写接口地址和模型。";

    public static string PresetSwitched(string displayName) => $"已切换为「{displayName}」，点击「保存」后生效。";

    public static string LoadFailed(string message) => $"读取语音识别设置失败：{message}";

    public static string SaveFailed(string message) => $"保存语音识别设置失败：{message}";

    public static string TestFailed(string message) => $"测试连接失败：{message}";
}
