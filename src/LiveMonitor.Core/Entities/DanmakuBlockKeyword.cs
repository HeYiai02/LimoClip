namespace LiveMonitor.Core.Entities;

/// <summary>
/// 本地统一弹幕屏蔽词。V0.2 起接入实时弹幕过滤。
/// </summary>
public sealed class DanmakuBlockKeyword
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Keyword { get; set; } = string.Empty;

    /// <summary>true 时按 .NET 正则表达式匹配弹幕内容，否则按包含匹配。</summary>
    public bool IsRegex { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
