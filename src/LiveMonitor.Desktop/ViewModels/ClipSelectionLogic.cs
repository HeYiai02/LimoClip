namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台选区 / 跳转 / 播放控制的纯规则：安全可剪上界、选区最小长度收敛、
/// 以当前位置落入/出点、键盘步进、结尾重播、高能片段窗口。不依赖 Avalonia 与播放器。
/// </summary>
internal static class ClipSelectionLogic
{
    /// <summary>选区最小长度（秒）。</summary>
    internal const double MinSelectionSeconds = 0.2;

    /// <summary>细步进（秒）：方向键 / 精调。</summary>
    internal const double FineSeekSeconds = 1;

    /// <summary>粗步进（秒）：Shift + 方向键。</summary>
    internal const double CoarseSeekSeconds = 5;

    /// <summary>播放到结尾前多少秒视为已到结尾，播放时从 0 重新开始。</summary>
    internal const double EndRestartToleranceSeconds = 0.2;

    internal static readonly TimeSpan MinSelection = TimeSpan.FromSeconds(MinSelectionSeconds);

    /// <summary>选区两端时间。</summary>
    internal readonly record struct SelectionBounds(TimeSpan InPoint, TimeSpan OutPoint);

    /// <summary>
    /// 选区可操作上界：边录边剪安全区已建立时钳到安全可剪截止，否则为完整时长。
    /// </summary>
    internal static TimeSpan UpperBound(bool isLiveSession, TimeSpan safeClipDuration, TimeSpan duration)
        => isLiveSession && safeClipDuration > TimeSpan.Zero ? safeClipDuration : duration;

    /// <summary>
    /// 时间轴拖动/键盘设点后的统一收敛：两端夹到 [0, upperBound]，并保证至少
    /// <see cref="MinSelection"/>；末尾不足时整体前移，仍不足则从 0 开始。
    /// </summary>
    internal static SelectionBounds NormalizeSelection(TimeSpan inPoint, TimeSpan outPoint, TimeSpan upperBound)
    {
        inPoint = Clamp(inPoint, TimeSpan.Zero, upperBound);
        outPoint = Clamp(outPoint, TimeSpan.Zero, upperBound);
        if (outPoint - inPoint < MinSelection)
        {
            outPoint = inPoint + MinSelection;
            if (outPoint > upperBound)
            {
                outPoint = upperBound;
                inPoint = upperBound - MinSelection;
                if (inPoint < TimeSpan.Zero)
                {
                    inPoint = TimeSpan.Zero;
                }
            }
        }

        return new SelectionBounds(inPoint, outPoint);
    }

    /// <summary>以当前播放位置落入点：夹到 [0,duration]，且不越过出点前的选区最小长度。</summary>
    internal static TimeSpan InPointFromPosition(TimeSpan position, TimeSpan outPoint, TimeSpan duration)
    {
        var inPoint = Clamp(position, TimeSpan.Zero, duration);
        if (inPoint > outPoint - MinSelection)
        {
            inPoint = outPoint - MinSelection;
        }

        return inPoint;
    }

    /// <summary>以当前播放位置落出点：夹到 [0,duration]，且不早于入点后的选区最小长度。</summary>
    internal static TimeSpan OutPointFromPosition(TimeSpan position, TimeSpan inPoint, TimeSpan duration)
    {
        var outPoint = Clamp(position, TimeSpan.Zero, duration);
        if (outPoint < inPoint + MinSelection)
        {
            outPoint = inPoint + MinSelection;
        }

        return outPoint;
    }

    /// <summary>键盘/按钮快进退的位移量：细步进 1s，粗步进 5s。</summary>
    internal static TimeSpan SeekOffset(bool forward, bool fine)
    {
        var step = TimeSpan.FromSeconds(fine ? FineSeekSeconds : CoarseSeekSeconds);
        return forward ? step : -step;
    }

    /// <summary>已到结尾附近再按播放时，应从 0 重新开始。</summary>
    internal static bool ShouldRestartFromStart(TimeSpan duration, TimeSpan position, bool isPlaying)
        => duration > TimeSpan.Zero
           && !isPlaying
           && position >= duration - TimeSpan.FromSeconds(EndRestartToleranceSeconds);

    /// <summary>
    /// 高能片段选区窗口：位置前后各展开 lead/tail，再夹到 [0,duration]。
    /// </summary>
    internal static SelectionBounds HighlightWindow(TimeSpan position, TimeSpan duration, TimeSpan lead, TimeSpan tail)
    {
        var start = position - lead;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var end = position + tail;
        if (end > duration)
        {
            end = duration;
        }

        return new SelectionBounds(start, end);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
