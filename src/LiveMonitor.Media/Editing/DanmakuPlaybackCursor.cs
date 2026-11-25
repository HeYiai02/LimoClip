using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 本地剪辑预览用的弹幕回放游标：把按 <see cref="RecordedDanmakuItem.OffsetMs"/> 排好序的
/// 录制弹幕映射到播放器当前时间轴上。
/// <para>
/// 语义：播放器每次拿到新的 <c>time-pos</c> 就调用 <see cref="AdvanceTo(long)"/>，
/// 游标只返回“上一次位置之后、本次位置之前”尚未推送过的弹幕（每条最多一次）；
/// 用户拖动进度条时调用 <see cref="SeekTo(long, long)"/>，除重定位外还会返回
/// 一小段回看窗口，避免画面瞬间空掉，同时不会一次刷出整屏。
/// </para>
/// 纯逻辑、无 UI 依赖，可被 Media.Tests 直接覆盖。
/// </summary>
public sealed class DanmakuPlaybackCursor
{
    /// <summary>seek 后默认补显窗口（毫秒）。</summary>
    public const long DefaultLookbackMs = 1500;

    /// <summary>单次 <see cref="AdvanceTo(long)"/> 最多返回多少条，防止大跳变时弹幕洪峰。</summary>
    public const int MaxBatchSize = 200;

    private RecordedDanmakuItem[] _items;
    private int _nextIndex;
    private long _positionMs = -1;

    public DanmakuPlaybackCursor(IReadOnlyList<RecordedDanmakuItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            _items = Array.Empty<RecordedDanmakuItem>();
            _nextIndex = 0;
            return;
        }

        _items = items.ToArray();
        // 正常录制产物本身有序；外部文件可能乱序，这里做一次稳定排序。
        Array.Sort(_items, static (a, b) => a.OffsetMs.CompareTo(b.OffsetMs));
        _nextIndex = 0;
    }

    /// <summary>弹幕总数。</summary>
    public int Count => _items.Length;

    /// <summary>
    /// 追加新录制到的弹幕（边录边剪时随录制进度增长），不影响当前播放位置：
    /// 追加后按偏移稳定排序，并把游标重新定位到当前位置之后，已播放过的位置不会被重放。
    /// </summary>
    public void Append(IReadOnlyList<RecordedDanmakuItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        var merged = new RecordedDanmakuItem[_items.Length + items.Count];
        Array.Copy(_items, merged, _items.Length);
        for (var i = 0; i < items.Count; i++)
        {
            merged[_items.Length + i] = items[i];
        }

        // 正常录制产物本身有序；追加跨批数据可能乱序，这里做一次稳定排序。
        Array.Sort(merged, static (a, b) => a.OffsetMs.CompareTo(b.OffsetMs));
        _items = merged;

        // 不重放已显示内容：把下一条待推送位置对齐到当前播放位置之后。
        _nextIndex = _positionMs < 0 ? 0 : UpperBound(_positionMs);
    }

    /// <summary>游标当前所在时间轴位置（毫秒），初始为 -1 表示尚未开始。</summary>
    public long PositionMs => _positionMs;

    /// <summary>
    /// 重定位到指定位置且不补显（下一次 <see cref="AdvanceTo(long)"/> 从该位置之后推送）。
    /// </summary>
    public void Reset(long positionMs = 0)
    {
        var position = Math.Max(0, positionMs);
        _positionMs = position;
        _nextIndex = UpperBound(position);
    }

    /// <summary>
    /// 跳到指定位置，返回应立刻补显的 <c>(position-lookback, position]</c> 区间弹幕。
    /// </summary>
    public IReadOnlyList<RecordedDanmakuItem> SeekTo(long positionMs, long lookbackMs = DefaultLookbackMs)
    {
        var position = Math.Max(0, positionMs);
        _positionMs = position;
        _nextIndex = UpperBound(position);

        if (lookbackMs <= 0 || _items.Length == 0)
        {
            return Array.Empty<RecordedDanmakuItem>();
        }

        var start = UpperBound(Math.Max(0, position - lookbackMs));
        var end = UpperBound(position);
        return end <= start
            ? Array.Empty<RecordedDanmakuItem>()
            : new ArraySegment<RecordedDanmakuItem>(_items, start, end - start);
    }

    /// <summary>
    /// 前进到指定位置，返回 <c>(上一次位置, position]</c> 内尚未推送的弹幕。
    /// 位置倒退时等价于 <see cref="Reset(long)"/>（不补显），避免播放器异常回跳时刷屏。
    /// </summary>
    public IReadOnlyList<RecordedDanmakuItem> AdvanceTo(long positionMs)
    {
        var position = Math.Max(0, positionMs);
        if (_positionMs >= 0 && position < _positionMs)
        {
            Reset(position);
            return Array.Empty<RecordedDanmakuItem>();
        }

        var end = UpperBound(position);
        if (end <= _nextIndex)
        {
            _positionMs = position;
            return Array.Empty<RecordedDanmakuItem>();
        }

        var start = _nextIndex;
        // 大跳变（例如 mpv 内部跳转）时只推送最近的一批，更早的丢弃。
        if (end - start > MaxBatchSize)
        {
            start = end - MaxBatchSize;
        }

        _nextIndex = end;
        _positionMs = position;
        return new ArraySegment<RecordedDanmakuItem>(_items, start, end - start);
    }

    private int UpperBound(long offsetMs)
    {
        var low = 0;
        var high = _items.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (_items[mid].OffsetMs <= offsetMs)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
