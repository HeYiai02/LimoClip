using System.Globalization;
using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku;

/// <summary>
/// 高能检测参数的持久化读写：把 <see cref="HighlightRuleOptions"/> 映射到 AppSettings 的 6 个
/// <see cref="AppSettingKeys"/> 键。读取时缺失或非法值一律回落到默认值并规范化；
/// 写入前先 <see cref="HighlightRuleOptions.Normalize"/>，关键词以换行分隔、按忽略大小写去重。
/// </summary>
public static class HighlightSettingsStore
{
    /// <summary>
    /// 从设置仓储读取高能检测参数。
    /// </summary>
    /// <param name="repository">设置仓储，不可为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>规范化后的参数；缺失或非法的键使用 <see cref="HighlightRuleOptions.Default"/> 对应值。</returns>
    public static async Task<HighlightRuleOptions> LoadAsync(
        IAppSettingsRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var values = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var defaults = HighlightRuleOptions.Default;

        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = ReadInt(values, AppSettingKeys.HighlightShortWindowSeconds, defaults.ShortWindowSeconds),
            BaselineWindowSeconds = ReadInt(values, AppSettingKeys.HighlightBaselineWindowSeconds, defaults.BaselineWindowSeconds),
            DensityMultiplier = ReadDouble(values, AppSettingKeys.HighlightDensityMultiplier, defaults.DensityMultiplier),
            MinShortWindowCount = ReadInt(values, AppSettingKeys.HighlightMinShortWindowCount, defaults.MinShortWindowCount),
            CooldownSeconds = ReadInt(values, AppSettingKeys.HighlightCooldownSeconds, defaults.CooldownSeconds),
            BoostKeywords = values.TryGetValue(AppSettingKeys.HighlightBoostKeywords, out var keywords)
                ? ParseBoostKeywords(keywords)
                : defaults.BoostKeywords
        };

        return options.Normalize();
    }

    /// <summary>
    /// 把高能检测参数写入设置仓储（6 个键，先规范化）。
    /// </summary>
    /// <param name="repository">设置仓储，不可为 null。</param>
    /// <param name="options">待保存的参数，不可为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task SaveAsync(
        IAppSettingsRepository repository,
        HighlightRuleOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);

        var normalized = options.Normalize();
        var invariant = CultureInfo.InvariantCulture;

        await repository.SetAsync(
            AppSettingKeys.HighlightShortWindowSeconds,
            normalized.ShortWindowSeconds.ToString(invariant),
            cancellationToken).ConfigureAwait(false);
        await repository.SetAsync(
            AppSettingKeys.HighlightBaselineWindowSeconds,
            normalized.BaselineWindowSeconds.ToString(invariant),
            cancellationToken).ConfigureAwait(false);
        await repository.SetAsync(
            AppSettingKeys.HighlightDensityMultiplier,
            normalized.DensityMultiplier.ToString("R", invariant),
            cancellationToken).ConfigureAwait(false);
        await repository.SetAsync(
            AppSettingKeys.HighlightMinShortWindowCount,
            normalized.MinShortWindowCount.ToString(invariant),
            cancellationToken).ConfigureAwait(false);
        await repository.SetAsync(
            AppSettingKeys.HighlightCooldownSeconds,
            normalized.CooldownSeconds.ToString(invariant),
            cancellationToken).ConfigureAwait(false);
        await repository.SetAsync(
            AppSettingKeys.HighlightBoostKeywords,
            FormatBoostKeywords(normalized.BoostKeywords),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把关键词集合格式化为一行一个的存储字符串（去空白、忽略大小写去重）。
    /// </summary>
    public static string FormatBoostKeywords(IEnumerable<string>? keywords)
    {
        if (keywords is null)
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            var trimmed = keyword.Trim();
            if (seen.Add(trimmed))
            {
                lines.Add(trimmed);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 解析存储字符串中的关键词（支持 \n 与 \r\n 换行，去空白、忽略大小写去重）。
    /// </summary>
    public static IReadOnlyList<string> ParseBoostKeywords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = new List<string>();
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }

            keywords.Add(trimmed);
        }

        return keywords;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (values.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
    {
        if (values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
