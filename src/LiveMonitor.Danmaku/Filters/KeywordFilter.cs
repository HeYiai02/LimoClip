using System.Text.RegularExpressions;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Filters;

/// <summary>
/// 跨平台关键词/正则过滤器。普通词按 <see cref="StringComparison.OrdinalIgnoreCase"/> 包含匹配，
/// 正则词使用编译缓存且带 100ms 匹配超时；非法正则与匹配超时都只记录一次日志并跳过，不会让弹幕管线崩溃。
/// <para>
/// 词库通过 <see cref="Update"/> 热更新：每次构建一份不可变快照并原子替换引用，
/// 读路径无锁，可安全地被多个接收线程并发调用。
/// </para>
/// </summary>
public sealed class KeywordFilter : IDanmakuFilter
{
    /// <summary>单条正则匹配允许的最长耗时，超过后跳过本次匹配，避免灾难性回溯拖垮接收线程。</summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    private sealed record KeywordSnapshot(IReadOnlyList<string> ContainsWords, IReadOnlyList<Regex> RegexPatterns);

    private static readonly KeywordSnapshot EmptySnapshot = new(Array.Empty<string>(), Array.Empty<Regex>());

    private readonly Action<string>? _log;
    private volatile KeywordSnapshot _snapshot;
    private int _regexTimeoutLogged;

    /// <summary>
    /// 用初始屏蔽词库创建过滤器。
    /// </summary>
    /// <param name="keywords">初始屏蔽词；空词会被忽略，非法正则会被跳过。</param>
    /// <param name="log">可选日志回调（例如输出到弹幕日志面板）。</param>
    public KeywordFilter(IEnumerable<DanmakuBlockKeyword> keywords, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        _log = log;
        _snapshot = EmptySnapshot;
        Update(keywords);
    }

    /// <summary>
    /// 热更新词库。旧快照会立即整体替换，调用返回后新词即时生效。
    /// </summary>
    /// <param name="keywords">完整的新词库列表（不是增量）。</param>
    public void Update(IEnumerable<DanmakuBlockKeyword> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var containsWords = new List<string>();
        var regexPatterns = new List<Regex>();
        var seenPlainWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRegexPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var keyword in keywords)
        {
            var text = keyword.Keyword;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            text = text.Trim();

            if (keyword.IsRegex)
            {
                if (!seenRegexPatterns.Add(text))
                {
                    continue;
                }

                try
                {
                    regexPatterns.Add(new Regex(
                        text,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        RegexMatchTimeout));
                }
                catch (ArgumentException ex)
                {
                    // 非法正则只跳过该词，不能让设置页的一条脏数据打死整个过滤器。
                    _log?.Invoke($"忽略非法正则屏蔽词「{text}」：{ex.Message}");
                }
            }
            else if (seenPlainWords.Add(text))
            {
                containsWords.Add(text);
            }
        }

        Interlocked.Exchange(ref _regexTimeoutLogged, 0);
        _snapshot = new KeywordSnapshot(containsWords, regexPatterns);
    }

    /// <summary>
    /// 判断弹幕是否放行。命中任意普通词或正则词返回 false。
    /// </summary>
    public bool ShouldAllow(DanmakuMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var content = message.Content;
        if (string.IsNullOrEmpty(content))
        {
            return true;
        }

        var snapshot = _snapshot;

        foreach (var word in snapshot.ContainsWords)
        {
            if (content.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var regex in snapshot.RegexPatterns)
        {
            try
            {
                if (regex.IsMatch(content))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 单条正则超时只跳过本次判断（放行），日志只记一次，避免刷屏。
                if (Interlocked.Exchange(ref _regexTimeoutLogged, 1) == 0)
                {
                    _log?.Invoke("正则屏蔽词匹配超时（>100ms），已跳过该次匹配。");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 校验正则语法是否合法，供设置页在保存前提示用户。空白模式视为非法。
    /// </summary>
    public static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexMatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
