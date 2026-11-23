namespace LiveMonitor.Core.Models;

/// <summary>
/// 高能密度检测参数。默认值来自 V0.2 设计文档；
/// 用户修改后由设置页持久化到 AppSettings。
/// </summary>
public sealed record HighlightRuleOptions
{
    /// <summary>默认短窗：最近 10 秒的密度与基线比较。</summary>
    public const int DefaultShortWindowSeconds = 10;

    /// <summary>默认基线窗：最近 180 秒的平均密度。</summary>
    public const int DefaultBaselineWindowSeconds = 180;

    /// <summary>默认倍数阈值：短窗密度达到基线的 3 倍触发。</summary>
    public const double DefaultDensityMultiplier = 3.0;

    /// <summary>短窗内绝对条数下限，防止冷清房基线极低时误报。</summary>
    public const int DefaultMinShortWindowCount = 15;

    /// <summary>触发后的冷却时间，避免同一波高能连续刷事件。</summary>
    public const int DefaultCooldownSeconds = 60;

    /// <summary>加权关键词，命中后每条按 +0.5 计入短窗密度。</summary>
    public static readonly IReadOnlyList<string> DefaultBoostKeywords = new[] { "666", "草", "神回", "Pog" };

    public static HighlightRuleOptions Default { get; } = new();

    public int ShortWindowSeconds { get; init; } = DefaultShortWindowSeconds;

    public int BaselineWindowSeconds { get; init; } = DefaultBaselineWindowSeconds;

    /// <summary>短窗密度 ÷ 基线密度 的触发倍数。</summary>
    public double DensityMultiplier { get; init; } = DefaultDensityMultiplier;

    public int MinShortWindowCount { get; init; } = DefaultMinShortWindowCount;

    public int CooldownSeconds { get; init; } = DefaultCooldownSeconds;

    public IReadOnlyList<string> BoostKeywords { get; init; } = DefaultBoostKeywords;

    /// <summary>
    /// 把外部输入（设置页、数据库）夹到合法范围，避免除零、负窗口等非法参数进入检测器。
    /// </summary>
    public HighlightRuleOptions Normalize()
    {
        var shortWindow = Math.Clamp(ShortWindowSeconds, 1, 600);
        var baselineWindow = Math.Clamp(BaselineWindowSeconds, shortWindow, 3600);
        var multiplier = double.IsFinite(DensityMultiplier)
            ? Math.Clamp(DensityMultiplier, 0.1, 1000)
            : DefaultDensityMultiplier;
        var minCount = Math.Clamp(MinShortWindowCount, 1, 1_000_000);
        var cooldown = Math.Clamp(CooldownSeconds, 0, 86_400);

        var rawKeywords = BoostKeywords ?? Array.Empty<string>();
        var keywords = rawKeywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return this with
        {
            ShortWindowSeconds = shortWindow,
            BaselineWindowSeconds = baselineWindow,
            DensityMultiplier = multiplier,
            MinShortWindowCount = minCount,
            CooldownSeconds = cooldown,
            BoostKeywords = keywords
        };
    }
}
