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

    // V0.3 录制中心
    /// <summary>录像默认保存目录；缺省为 %UserProfile%\Videos\LimoClip。</summary>
    public const string RecordingDefaultDirectory = "RecordingDefaultDirectory";

    /// <summary>自动录制的开播轮询间隔（秒），默认 60；取值 10~600。</summary>
    public const string RecordingPollIntervalSeconds = "RecordingPollIntervalSeconds";

    // V0.5-A 剪辑台云端 ASR（设置页持久化；API Key 经 DpapiCookieProtector 加密）
    /// <summary>ASR 预设 ID：siliconflow_cn / siliconflow_global / groq / custom。</summary>
    public const string AsrProviderPreset = "AsrProviderPreset";

    /// <summary>ASR 转写接口完整地址（OpenAI 兼容 /v1/audio/transcriptions）。</summary>
    public const string AsrEndpoint = "AsrEndpoint";

    /// <summary>ASR 模型名，如 FunAudioLLM/SenseVoiceSmall、whisper-large-v3-turbo。</summary>
    public const string AsrModel = "AsrModel";

    /// <summary>Windows DPAPI 加密后的 API Key（Base64），只能由 Data 层 DpapiCookieProtector 解密。</summary>
    public const string AsrEncryptedApiKey = "AsrEncryptedApiKey";

    /// <summary>识别语言；默认 auto（自动检测），与 OpenAI 兼容接口的 language 参数对应。</summary>
    public const string AsrLanguage = "AsrLanguage";

    // V0.5-G Twitch 去广告（方案 B）
    /// <summary>Twitch 播放列表代理模板（可选）；支持 {channel} 占位符，如 https://eu.luminous.dev/live/{channel} 或 https://proxy.example.com/{channel}.m3u8。留空则不走外部代理。</summary>
    public const string TwitchPlaylistProxyUrl = "twitch.playlist_proxy_url";
}
