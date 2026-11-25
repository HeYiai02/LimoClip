namespace LiveMonitor.Desktop.Services;

/// <summary>单个窗格的一次健康采样：PaneId 与播放器上报的累计丢帧数/当前帧率。</summary>
internal readonly record struct PaneHealthSample(string PaneId, long DroppedFrames, double FramesPerSecond);

/// <summary>一次健康刷新的聚合结果：本周期丢帧、期望帧数、丢帧率、告警标记与状态栏文案。</summary>
internal readonly record struct HealthSummary(
    long DroppedFrames,
    double ExpectedFrames,
    double DropPercent,
    bool IsWarning,
    string Text);

/// <summary>
/// 解码健康统计的纯状态机：按窗格记录「上一次累计丢帧数」作为基线，
/// 每次刷新对同一窗格做差、移除已消失窗格的基线，再算出丢帧率与告警文案。
/// 不依赖计时器/播放器/UI，由 MainWindowViewModel 负责采样与属性通知。
/// </summary>
internal sealed class HealthSummaryCoordinator
{
    /// <summary>丢帧率达到该百分比即视为解码告警。</summary>
    public const double WarningThresholdPercent = 5.0;

    private readonly Dictionary<string, long> _lastDropCounts = new(StringComparer.Ordinal);

    /// <summary>用本周期采样刷新统计；首次出现的窗格只建立基线，不产生丢帧。</summary>
    public HealthSummary Update(IEnumerable<PaneHealthSample> samples)
    {
        long dropped = 0;
        double frames = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sample in samples)
        {
            if (!_lastDropCounts.TryGetValue(sample.PaneId, out var previous))
            {
                _lastDropCounts[sample.PaneId] = sample.DroppedFrames;
                seen.Add(sample.PaneId);
                continue;
            }

            dropped += Math.Max(0, sample.DroppedFrames - previous);
            frames += Math.Max(1, sample.FramesPerSecond) * 2;
            _lastDropCounts[sample.PaneId] = sample.DroppedFrames;
            seen.Add(sample.PaneId);
        }

        foreach (var stale in _lastDropCounts.Keys.Except(seen).ToList())
        {
            _lastDropCounts.Remove(stale);
        }

        var percent = frames > 0 ? dropped / frames * 100 : 0;
        var isWarning = percent >= WarningThresholdPercent;
        var text = isWarning
            ? $"解码告警 ({percent:F1}% 丢帧)"
            : $"解码正常 ({percent:F0}% 丢帧)";
        return new HealthSummary(dropped, frames, percent, isWarning, text);
    }
}
