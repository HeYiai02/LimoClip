using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Analyzers;

/// <summary>
/// 按房间统计弹幕密度的高能检测器。
/// <para>
/// 每个 (平台, 房间) 维护一组 1 秒粒度的环形桶（长度 = 基线窗秒数），
/// 普通弹幕计 1 票，内容命中任一加权关键词（忽略大小写）再 +0.5 票（每条消息最多加一次）。
/// 当短窗真实条数达到下限、且短窗密度达到基线密度的指定倍数、并已过冷却时间时触发
/// <see cref="HighlightTriggered"/> 事件。环形桶靠时间范围过滤天然老化，无需逐秒清理。
/// </para>
/// </summary>
public sealed class SlidingWindowDetector : IHighlightDetector
{
    /// <summary>命中加权关键词的额外权重。</summary>
    public const double BoostKeywordWeight = 0.5;

    private sealed class RoomState
    {
        public RoomState(int bucketCount)
        {
            BucketSeconds = new long[bucketCount];
            BucketWeights = new double[bucketCount];
            BucketRawCounts = new int[bucketCount];
        }

        /// <summary>桶对应的 Unix 秒；0 表示空闲桶（Unix 纪元前的时间不会出现）。</summary>
        public long[] BucketSeconds { get; }

        public double[] BucketWeights { get; }

        public int[] BucketRawCounts { get; }

        public DateTimeOffset LastTrigger { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(PlatformType Platform, string RoomId), RoomState> _rooms = new();
    private readonly Func<DateTimeOffset> _clock;
    private HighlightRuleOptions _rules;

    /// <summary>
    /// 创建检测器。
    /// </summary>
    /// <param name="options">检测参数，创建时会先调用 <see cref="HighlightRuleOptions.Normalize"/> 夹取非法值。</param>
    /// <param name="clock">可选时钟（默认系统 UTC 时间），注入后便于测试窗口老化与冷却。</param>
    public SlidingWindowDetector(HighlightRuleOptions options, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rules = options.Normalize();
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public event Action<HighlightEvent>? HighlightTriggered;

    /// <summary>当前生效的检测参数（已规范化）。</summary>
    public HighlightRuleOptions CurrentRules
    {
        get
        {
            lock (_gate)
            {
                return _rules;
            }
        }
    }

    /// <inheritdoc />
    public void Ingest(DanmakuMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        HighlightEvent? highlight = null;
        Action<HighlightEvent>? handler = null;

        lock (_gate)
        {
            var now = _clock();
            var rules = _rules;
            var roomKey = (message.Platform, message.RoomId);
            if (!_rooms.TryGetValue(roomKey, out var state))
            {
                state = new RoomState(rules.BaselineWindowSeconds);
                _rooms.Add(roomKey, state);
            }

            var second = now.ToUnixTimeSeconds();
            var bucketCount = state.BucketSeconds.Length;
            var index = (int)(((second % bucketCount) + bucketCount) % bucketCount);
            if (state.BucketSeconds[index] != second)
            {
                state.BucketSeconds[index] = second;
                state.BucketWeights[index] = 0;
                state.BucketRawCounts[index] = 0;
            }

            state.BucketWeights[index] += GetMessageWeight(message.Content, rules.BoostKeywords);
            state.BucketRawCounts[index]++;

            var baselineCutoff = second - rules.BaselineWindowSeconds + 1;
            var shortCutoff = second - rules.ShortWindowSeconds + 1;
            double baselineWeighted = 0;
            double shortWeighted = 0;
            var rawShortCount = 0;
            for (var i = 0; i < bucketCount; i++)
            {
                var bucketSecond = state.BucketSeconds[i];
                if (bucketSecond > second || bucketSecond < baselineCutoff)
                {
                    continue;
                }

                baselineWeighted += state.BucketWeights[i];
                if (bucketSecond >= shortCutoff)
                {
                    shortWeighted += state.BucketWeights[i];
                    rawShortCount += state.BucketRawCounts[i];
                }
            }

            if (rawShortCount < rules.MinShortWindowCount)
            {
                return;
            }

            var baselineRate = baselineWeighted / rules.BaselineWindowSeconds;
            var shortRate = shortWeighted / rules.ShortWindowSeconds;
            if (baselineRate <= 0 || shortRate < baselineRate * rules.DensityMultiplier)
            {
                return;
            }

            if (now - state.LastTrigger < TimeSpan.FromSeconds(rules.CooldownSeconds))
            {
                return;
            }

            state.LastTrigger = now;
            var ratio = shortRate / baselineRate;
            var reason = FormattableString.Invariant(
                $"最近 {rules.ShortWindowSeconds} 秒弹幕 {rawShortCount} 条，密度 {shortRate:F2} 条/秒，达到 {rules.BaselineWindowSeconds} 秒基线 {baselineRate:F2} 条/秒 的 {ratio:F1} 倍");
            highlight = new HighlightEvent(message.Platform, message.RoomId, now, shortRate, baselineRate, reason);
            handler = HighlightTriggered;
        }

        // 事件在锁外触发，避免订阅方回调时重入或长时间持锁。
        if (highlight is not null && handler is not null)
        {
            handler(highlight);
        }
    }

    /// <inheritdoc />
    public void UpdateRules(HighlightRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options.Normalize();

        lock (_gate)
        {
            // 窗口尺寸变化会让旧环形桶的统计口径失效，必须清空；其余参数热更新即可保留窗口数据。
            if (normalized.ShortWindowSeconds != _rules.ShortWindowSeconds
                || normalized.BaselineWindowSeconds != _rules.BaselineWindowSeconds)
            {
                _rooms.Clear();
            }

            _rules = normalized;
        }
    }

    /// <summary>清空所有房间的窗口与冷却状态（房间全部关闭或设置重置时调用）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _rooms.Clear();
        }
    }

    private static double GetMessageWeight(string content, IReadOnlyList<string> boostKeywords)
    {
        if (boostKeywords.Count == 0 || string.IsNullOrEmpty(content))
        {
            return 1.0;
        }

        foreach (var keyword in boostKeywords)
        {
            if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                // 每条消息最多只加一次权，命中多个关键词不叠加。
                return 1.0 + BoostKeywordWeight;
            }
        }

        return 1.0;
    }
}
