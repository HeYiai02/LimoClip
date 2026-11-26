namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 语音识别服务商预设目录：预设元数据（名称/接口/模型/说明）的唯一来源。
/// 设置页只读取这里，避免文案与地址散落在 Store 与视图中。
/// </summary>
internal static class AsrPresetCatalog
{
    /// <summary>默认预设（硅基流动国内站）。</summary>
    public const string DefaultPresetId = "siliconflow_cn";

    /// <summary>全部预设，顺序即设置页展示顺序；最后一个为自定义 OpenAI 兼容服务。</summary>
    public static readonly IReadOnlyList<AsrPreset> Presets = new[]
    {
        new AsrPreset(
            "siliconflow_cn",
            "硅基流动（国内）",
            "https://api.siliconflow.cn/v1/audio/transcriptions",
            "FunAudioLLM/SenseVoiceSmall",
            "整段文本返回（json），无逐句时间戳；国内直连，中文识别效果好。"),
        new AsrPreset(
            "siliconflow_global",
            "硅基流动（国际）",
            "https://api.siliconflow.com/v1/audio/transcriptions",
            "FunAudioLLM/SenseVoiceSmall",
            "与国内站相同模型，适合海外网络。"),
        new AsrPreset(
            "groq",
            "Groq",
            "https://api.groq.com/openai/v1/audio/transcriptions",
            "whisper-large-v3-turbo",
            "verbose_json 返回逐句时间戳，可点击跳转；有免费额度。"),
        new AsrPreset(
            "custom",
            "自定义（OpenAI 兼容）",
            string.Empty,
            string.Empty,
            "手动填写转写接口地址与模型名；优先请求 verbose_json，不支持时自动回退纯文本。"),
    };

    /// <summary>按 ID 解析预设；未知或空白回退到 <see cref="DefaultPresetId"/> 对应预设。</summary>
    public static AsrPreset Resolve(string? presetId)
    {
        if (!string.IsNullOrWhiteSpace(presetId))
        {
            foreach (var preset in Presets)
            {
                if (string.Equals(preset.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return preset;
                }
            }
        }

        return Presets[0];
    }
}
