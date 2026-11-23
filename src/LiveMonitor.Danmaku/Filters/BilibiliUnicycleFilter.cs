using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Filters;

/// <summary>
/// 仅对 B 站生效的单用户独轮车（机械复读/定时脚本）过滤器。
/// <para>
/// 只检查“同一个房间 + 同一个发送者”的滑窗，不跨用户、不跨房间；
/// 游客态（uid="0"/空）与礼物、醒目留言全部放行，避免脱敏 uid 导致全房误杀；
/// 清洗后长度 ≤6 的短情绪词（666、哈哈哈、？？？等）永不拦截。
/// </para>
/// <para>
/// 规则命中后该用户会被静默 10 分钟，静默期内其消息全部拦截；
/// 用户状态按“每 2000 条消息或每 5 分钟”惰性驱逐 15 分钟无发言的 key，并设置 5 万字典上限防止挂机泄漏。
/// 所有时间判定使用注入时钟，内部加锁，可被多条接收线程并发调用。
/// </para>
/// </summary>
public sealed class BilibiliUnicycleFilter : IDanmakuFilter
{
    /// <summary>清洗后长度不超过该值的弹幕视为短情绪词，永不拦截。</summary>
    public const int ShortEmotionMaxLength = 6;

    /// <summary>单用户状态字典的硬上限，超过后按最近发言时间清理最旧记录。</summary>
    public const int MaxTrackedUsers = 50_000;

    /// <summary>每处理这么多条 B 站有效弹幕做一次惰性驱逐。</summary>
    private const int EvictionMessageInterval = 2_000;

    private const int LongTextMinLength = 15;        // 长文本规则：清洗/原始长度 > 15
    private const int IdenticalMinLength = 10;       // 完全一致规则：清洗后长度 > 10
    private const double SimilarityThreshold = 0.80; // 清洗后相似度阈值
    private const double IntervalStdDevMaxSeconds = 0.5; // 最近 4 条同类发言间隔标准差上限

    private static readonly TimeSpan Window60Seconds = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window30Seconds = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Window15Seconds = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MuteDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdleEvictThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EvictionCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>单用户最多保留的 60 秒窗口消息条数，极端刷屏时限制内存与比对成本。</summary>
    private const int MaxHistoryPerUser = 512;

    private readonly record struct UserMsgRecord(string NormalizedText, int RawLength, DateTimeOffset Timestamp);

    private sealed class UserState
    {
        public Queue<UserMsgRecord> RecentMessages { get; } = new();

        public DateTimeOffset MutedUntil { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastActive { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, UserState> _users = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    private long _processedCount;
    private long _blockedCount;
    private DateTimeOffset _lastEvictionAt = DateTimeOffset.MinValue;
    private volatile bool _isEnabled = true;

    /// <summary>
    /// 创建 B 站独轮车过滤器。
    /// </summary>
    /// <param name="clock">可选时钟（默认系统 UTC 时间），注入后便于测试时间窗口与静默/驱逐逻辑。</param>
    public BilibiliUnicycleFilter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>是否启用独轮车过滤。关闭时全部放行，已有用户状态保留。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>当前被跟踪的 (房间 + 用户) 状态数量，用于诊断内存占用。</summary>
    public int TrackedUserCount
    {
        get
        {
            lock (_gate)
            {
                return _users.Count;
            }
        }
    }

    /// <summary>累计拦截条数（含静默期内被拦截的消息）。</summary>
    public long BlockedCount => Interlocked.Read(ref _blockedCount);

    /// <summary>
    /// 清空所有用户状态与计数，用于设置变更或房间全部关闭后的重置。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _users.Clear();
            _processedCount = 0;
            Interlocked.Exchange(ref _blockedCount, 0);
            _lastEvictionAt = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc />
    public bool ShouldAllow(DanmakuMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_isEnabled)
        {
            return true;
        }

        // 非 B 站平台完全不走独轮车判定（Twitch/YouTube 等只过 KeywordFilter）。
        if (message.Platform != PlatformType.Bilibili)
        {
            return true;
        }

        // 礼物 / 醒目留言不是普通弹幕，放行。
        if (message.IsGiftOrSuperChat)
        {
            return true;
        }

        // 游客态 uid 全为 "0"，按用户归组会把全房当成同一人，必须跳过。
        if (message.IsAnonymousSender)
        {
            return true;
        }

        lock (_gate)
        {
            var now = _clock();
            _processedCount++;
            MaybeEvict(now);

            var key = string.Concat(message.RoomId, ":", message.SenderUid);
            if (!_users.TryGetValue(key, out var state))
            {
                state = new UserState();
                _users.Add(key, state);
                EnforceCapacity();
            }

            state.LastActive = now;

            // 静默期内该用户的所有消息一律拦截。
            if (now < state.MutedUntil)
            {
                Interlocked.Increment(ref _blockedCount);
                return false;
            }

            var normalized = NormalizeText(message.Content);
            if (normalized.Length == 0)
            {
                // 纯标点/表情等清洗后无内容的弹幕视为短情绪词，放行。
                return true;
            }

            // 只保留最近 60 秒的记录。
            var cutoff = now - Window60Seconds;
            while (state.RecentMessages.Count > 0 && state.RecentMessages.Peek().Timestamp < cutoff)
            {
                state.RecentMessages.Dequeue();
            }

            state.RecentMessages.Enqueue(new UserMsgRecord(
                normalized,
                message.Content.Trim().Length,
                now));

            while (state.RecentMessages.Count > MaxHistoryPerUser)
            {
                state.RecentMessages.Dequeue();
            }

            // 硬性豁免：≤6 字的短情绪词永不拦截，也不作为机械复读的证据。
            if (normalized.Length <= ShortEmotionMaxLength)
            {
                return true;
            }

            // 规则 2：30 秒内 ≥3 条长度 > 15 字的长文本（小说/文案切片特征）。
            if (IsLongTextBurst(state.RecentMessages, now))
            {
                MuteAndBlock(state, now);
                return false;
            }

            // 规则 3：15 秒内 ≥3 条清洗后完全一致且长度 > 10 的长句。
            if (normalized.Length > IdenticalMinLength &&
                IsIdenticalRepeat(state.RecentMessages, normalized, now))
            {
                MuteAndBlock(state, now);
                return false;
            }

            // 规则 1：60 秒内 >3 条相似度 >80%，或最近 4 条发言间隔标准差 <0.5 秒。
            if (IsMechanicalRepeat(state.RecentMessages, normalized, now))
            {
                MuteAndBlock(state, now);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 文本清洗：去除空白、标点、符号及常见防屏蔽干扰符，仅保留 <see cref="char.IsLetterOrDigit(char)"/> 并转小写。
    /// </summary>
    public static string NormalizeText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 基于 Levenshtein 编辑距离的归一化相似度，取值范围 [0, 1]。
    /// </summary>
    public static double ComputeSimilarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var maxLength = Math.Max(a.Length, b.Length);
        if (maxLength == 0)
        {
            return 1.0;
        }

        return 1.0 - (double)LevenshteinDistance(a, b) / maxLength;
    }

    private void MuteAndBlock(UserState state, DateTimeOffset now)
    {
        state.MutedUntil = now + MuteDuration;
        Interlocked.Increment(ref _blockedCount);
    }

    private static bool IsLongTextBurst(Queue<UserMsgRecord> records, DateTimeOffset now)
    {
        var longCount = 0;
        foreach (var record in records)
        {
            if (now - record.Timestamp <= Window30Seconds && record.RawLength > LongTextMinLength)
            {
                longCount++;
                if (longCount >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsIdenticalRepeat(Queue<UserMsgRecord> records, string currentNormalized, DateTimeOffset now)
    {
        var identicalCount = 0;
        foreach (var record in records)
        {
            if (now - record.Timestamp > Window15Seconds)
            {
                continue;
            }

            if (record.NormalizedText.Length > IdenticalMinLength &&
                string.Equals(record.NormalizedText, currentNormalized, StringComparison.Ordinal))
            {
                identicalCount++;
                if (identicalCount >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsMechanicalRepeat(Queue<UserMsgRecord> records, string currentNormalized, DateTimeOffset now)
    {
        var similarCount = 0;
        var recentNonShortTimestamps = new List<DateTimeOffset>(5);

        foreach (var record in records)
        {
            if (now - record.Timestamp > Window60Seconds)
            {
                continue;
            }

            // 短情绪词不计入机械复读证据，避免误伤正常互动。
            if (record.NormalizedText.Length <= ShortEmotionMaxLength)
            {
                continue;
            }

            if (IsSimilar(record.NormalizedText, currentNormalized))
            {
                similarCount++;
                if (similarCount > 3)
                {
                    return true;
                }
            }

            recentNonShortTimestamps.Add(record.Timestamp);
            if (recentNonShortTimestamps.Count > 4)
            {
                recentNonShortTimestamps.RemoveAt(0);
            }
        }

        // 规则 1B：最近连续 4 条非短词发言的间隔标准差 < 0.5 秒（脚本定时器特征）。
        if (recentNonShortTimestamps.Count == 4)
        {
            var interval1 = (recentNonShortTimestamps[1] - recentNonShortTimestamps[0]).TotalSeconds;
            var interval2 = (recentNonShortTimestamps[2] - recentNonShortTimestamps[1]).TotalSeconds;
            var interval3 = (recentNonShortTimestamps[3] - recentNonShortTimestamps[2]).TotalSeconds;
            var mean = (interval1 + interval2 + interval3) / 3.0;
            var variance = ((interval1 - mean) * (interval1 - mean)
                + (interval2 - mean) * (interval2 - mean)
                + (interval3 - mean) * (interval3 - mean)) / 3.0;
            if (Math.Sqrt(variance) < IntervalStdDevMaxSeconds)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSimilar(string a, string b)
    {
        var maxLength = Math.Max(a.Length, b.Length);
        if (maxLength == 0)
        {
            return true;
        }

        // 长度差异已经让相似度上限低于阈值时直接剪枝，避免无谓的 O(n*m) 计算。
        if ((double)Math.Min(a.Length, b.Length) / maxLength <= SimilarityThreshold)
        {
            return false;
        }

        return ComputeSimilarity(a, b) > SimilarityThreshold;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private void MaybeEvict(DateTimeOffset now)
    {
        var byCount = _processedCount % EvictionMessageInterval == 0;
        var byTime = now - _lastEvictionAt >= EvictionCheckInterval;
        if (!byCount && !byTime)
        {
            return;
        }

        EvictStale(now);
        _lastEvictionAt = now;
    }

    private void EvictStale(DateTimeOffset now)
    {
        List<string>? staleKeys = null;
        foreach (var pair in _users)
        {
            // 静默期未结束的不过早清理，避免同一个人换个房间继续刷时丢掉静默状态。
            if (now - pair.Value.LastActive > IdleEvictThreshold && now >= pair.Value.MutedUntil)
            {
                (staleKeys ??= new List<string>()).Add(pair.Key);
            }
        }

        if (staleKeys is null)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            _users.Remove(key);
        }
    }

    private void EnforceCapacity()
    {
        if (_users.Count <= MaxTrackedUsers)
        {
            return;
        }

        // 超限时一次性清到 90% 水位，避免每来一条新用户都排序一遍。
        var removeCount = _users.Count - MaxTrackedUsers + MaxTrackedUsers / 10;
        var oldestKeys = _users
            .OrderBy(pair => pair.Value.LastActive)
            .Take(removeCount)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in oldestKeys)
        {
            _users.Remove(key);
        }
    }
}
