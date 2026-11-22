namespace LiveMonitor.Core.Entities;

/// <summary>
/// 全局键值配置，例如 BlockUnicycleEnabled、DanmakuPreviewEnabled。
/// </summary>
public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
