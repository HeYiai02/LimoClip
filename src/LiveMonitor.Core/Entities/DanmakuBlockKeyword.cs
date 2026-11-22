namespace LiveMonitor.Core.Entities;

/// <summary>
/// 本地统一弹幕屏蔽词。V0.1 只负责配置入库，V0.3 才接入实时弹幕过滤。
/// </summary>
public sealed class DanmakuBlockKeyword
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Keyword { get; set; } = string.Empty;

    /// <summary>V0.1 固定为 false；字段预留给 V0.3 的正则词条。</summary>
    public bool IsRegex { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
