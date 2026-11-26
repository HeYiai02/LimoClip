using System.Globalization;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 回放进度条的纯交互逻辑：把 mpv 探测结果（seekable / duration / time-pos）换算成
/// 「是否显示进度条、滑块上限与位置、时间文本」，并管理拖动防抖后待提交的 seek 目标。
/// 与 mpv 上下文、Avalonia 控件和计时器解耦，便于单元测试（V0.7.5）。
/// 我们不做本地时移缓冲，能回放多少完全取决于源站能力。
/// </summary>
internal sealed class ReplaySeekLogic
{
    /// <summary>可回放源的最小时长门槛：不足 1 秒的 duration 视为探测噪声，不显示进度条。</summary>
    public const double MinimumSeekableDurationSeconds = 1d;

    /// <summary>滑块上限与探测时长的最小更新阈值，避免每 0.5s 重复写同一个值。</summary>
    public const double MaximumToleranceSeconds = 0.01d;

    /// <summary>轮询回写滑块位置的最小阈值，避免进度条被 0.5s 一次的探测抖动带偏。</summary>
    public const double PositionToleranceSeconds = 0.25d;

    /// <summary>时长探测轮询间隔。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>拖动结束后提交 seek 前的防抖间隔，避免一次拖动打出几十条命令。</summary>
    public static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(160);

    private double _pendingSeconds = -1;

    /// <summary>当前源是否支持回放（mpv 报 seekable=yes 且 duration 有限）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>当前源总时长（秒），不可回放时为 0。</summary>
    public double Duration { get; private set; }

    /// <summary>是否处于拖动中（已记录待提交的 seek 目标，但尚未提交）。</summary>
    public bool IsDragging => _pendingSeconds >= 0;

    /// <summary>判断 mpv 探测值是否表示可回放源，并返回其有限总时长。</summary>
    public static bool IsReplayable(string? seekable, double? duration, out double total)
    {
        total = 0;
        if (!string.Equals(seekable, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (duration is not { } value || !double.IsFinite(value) || value < MinimumSeekableDurationSeconds)
        {
            return false;
        }

        total = value;
        return true;
    }

    /// <summary>
    /// 根据一次探测结果更新状态，返回需要写回控件的增量。
    /// <paramref name="currentMaximum"/>/<paramref name="currentValue"/> 为滑块当前值：
    /// 差值不超过阈值的写入会被省略，避免每轮轮询都触发一次滑块变更。
    /// </summary>
    public SeekBarUpdate Update(string? seekable, double? duration, double? position, double currentMaximum, double currentValue)
    {
        if (!IsReplayable(seekable, duration, out var total))
        {
            var wasVisible = IsAvailable;
            Reset();
            return new SeekBarUpdate(false, wasVisible, null, null, null);
        }

        var visibilityChanged = !IsAvailable;
        IsAvailable = true;
        Duration = total;

        double? maximum = Math.Abs(currentMaximum - total) > MaximumToleranceSeconds ? total : null;

        // 用户正在拖动（提交定时器在跑）时不回写位置，避免进度条被轮询抢回去。
        double? value = null;
        var effectiveValue = currentValue;
        if (!IsDragging && position is { } current)
        {
            var clamped = Math.Clamp(current, 0d, total);
            effectiveValue = clamped;
            if (Math.Abs(currentValue - clamped) > PositionToleranceSeconds)
            {
                value = clamped;
            }
        }

        return new SeekBarUpdate(true, visibilityChanged, maximum, value, FormatPosition(effectiveValue, total));
    }

    /// <summary>记录一次拖动位置；防抖计时器结束后由 <see cref="TryTakePendingTarget"/> 取出并提交。</summary>
    public void BeginDrag(double seconds)
    {
        _pendingSeconds = double.IsFinite(seconds) ? Math.Max(0d, seconds) : 0d;
    }

    /// <summary>取出待提交的 seek 目标（mpv 命令参数格式）；没有待提交目标时返回 false。</summary>
    public bool TryTakePendingTarget(out string target)
    {
        target = string.Empty;
        if (_pendingSeconds < 0)
        {
            return false;
        }

        var seconds = _pendingSeconds;
        _pendingSeconds = -1;
        target = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>拖动时显示的时间文本（位置使用用户拖动到的值，总时长使用当前探测值）。</summary>
    public string FormatDragTime(double seconds) => FormatPosition(seconds, Duration);

    /// <summary>流切换/停止探测时清空状态。</summary>
    public void Reset()
    {
        IsAvailable = false;
        Duration = 0;
        _pendingSeconds = -1;
    }

    /// <summary>「当前位置 / 总时长」文本。</summary>
    public static string FormatPosition(double seconds, double duration) =>
        $"{FormatClock(seconds)} / {FormatClock(duration)}";

    /// <summary>秒数格式化为 mm:ss；超过 1 小时为 h:mm:ss；非法值或负数按 0 处理。</summary>
    public static string FormatClock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>一次探测后需要写回进度条控件的增量；为 null 的字段表示无需更新。</summary>
internal readonly record struct SeekBarUpdate(
    bool IsVisible,
    bool VisibilityChanged,
    double? Maximum,
    double? Value,
    string? TimeText);
