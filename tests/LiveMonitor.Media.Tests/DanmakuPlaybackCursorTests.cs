using LiveMonitor.Core.Models;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class DanmakuPlaybackCursorTests
{
    private static RecordedDanmakuItem Item(long offsetMs, string text)
        => new(offsetMs, "tester", text, IsSticker: false, IsGift: false);

    private static long[] Offsets(IReadOnlyList<RecordedDanmakuItem> items)
        => items.Select(item => item.OffsetMs).ToArray();

    [Fact]
    public void AdvanceTo_emits_each_item_once_in_order()
    {
        var cursor = new DanmakuPlaybackCursor(new[]
        {
            Item(1500, "c"),
            Item(0, "a"),
            Item(900, "b"),
            Item(3000, "e"),
            Item(2200, "d"),
        });

        Assert.Equal(new long[] { 0 }, Offsets(cursor.AdvanceTo(0)));
        Assert.Empty(Offsets(cursor.AdvanceTo(0)));
        Assert.Equal(new long[] { 900 }, Offsets(cursor.AdvanceTo(1000)));
        Assert.Empty(Offsets(cursor.AdvanceTo(1000)));
        Assert.Equal(new long[] { 1500, 2200 }, Offsets(cursor.AdvanceTo(2500)));
        Assert.Equal(new long[] { 3000 }, Offsets(cursor.AdvanceTo(5000)));
        Assert.Empty(Offsets(cursor.AdvanceTo(6000)));
    }

    [Fact]
    public void SeekTo_replays_lookback_and_skips_emitted_items()
    {
        var cursor = new DanmakuPlaybackCursor(new[]
        {
            Item(0, "a"), Item(500, "b"), Item(1000, "c"),
            Item(1500, "d"), Item(2000, "e"), Item(3000, "f"),
        });

        Assert.Equal(6, cursor.AdvanceTo(3000).Count);

        var replay = cursor.SeekTo(2000, lookbackMs: 1000);
        Assert.Equal(new long[] { 1500, 2000 }, Offsets(replay));
        Assert.Equal(2000, cursor.PositionMs);
        Assert.Empty(Offsets(cursor.AdvanceTo(2500)));
        Assert.Equal(new long[] { 3000 }, Offsets(cursor.AdvanceTo(3500)));
    }

    [Fact]
    public void SeekTo_without_lookback_emits_nothing_immediately()
    {
        var cursor = new DanmakuPlaybackCursor(new[] { Item(0, "a"), Item(1500, "b") });

        Assert.Empty(Offsets(cursor.SeekTo(1000, lookbackMs: 0)));
        Assert.Empty(Offsets(cursor.AdvanceTo(1200)));
        Assert.Equal(new long[] { 1500 }, Offsets(cursor.AdvanceTo(1600)));
    }

    [Fact]
    public void Backward_advance_repositions_without_bulk_replay()
    {
        var cursor = new DanmakuPlaybackCursor(new[] { Item(500, "a"), Item(900, "b"), Item(1500, "c") });

        Assert.Equal(new long[] { 500, 900, 1500 }, Offsets(cursor.AdvanceTo(2000)));
        // 倒退：不补显，但重定位到 300ms 之后。
        Assert.Empty(Offsets(cursor.AdvanceTo(300)));
        Assert.Equal(300, cursor.PositionMs);
        Assert.Equal(new long[] { 500, 900 }, Offsets(cursor.AdvanceTo(1000)));
    }

    [Fact]
    public void AdvanceTo_truncates_large_jumps_to_recent_batch()
    {
        var items = Enumerable.Range(0, 500)
            .Select(index => Item(index * 10L, index.ToString()))
            .ToArray();
        var cursor = new DanmakuPlaybackCursor(items);

        var batch = cursor.AdvanceTo(10_000);

        Assert.Equal(DanmakuPlaybackCursor.MaxBatchSize, batch.Count);
        Assert.Equal(3_000, batch[0].OffsetMs);
        Assert.Equal(4_990, batch[^1].OffsetMs);
    }

    [Fact]
    public void Empty_items_yield_nothing()
    {
        var empty = new DanmakuPlaybackCursor(null);
        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.AdvanceTo(1000));
        Assert.Empty(empty.SeekTo(1000));

        var noItems = new DanmakuPlaybackCursor(Array.Empty<RecordedDanmakuItem>());
        Assert.Empty(noItems.AdvanceTo(1000));
    }

    [Fact]
    public void Negative_positions_are_clamped_to_zero()
    {
        var cursor = new DanmakuPlaybackCursor(new[] { Item(0, "a") });

        Assert.Empty(Offsets(cursor.SeekTo(-500, lookbackMs: 0)));
        Assert.Equal(0, cursor.PositionMs);

        var fresh = new DanmakuPlaybackCursor(new[] { Item(0, "a") });
        Assert.Equal(new long[] { 0 }, Offsets(fresh.AdvanceTo(-100)));
    }

    [Fact]
    public void Append_keeps_order_and_does_not_replay_emitted_items()
    {
        var cursor = new DanmakuPlaybackCursor(new[] { Item(0, "a"), Item(1000, "b") });
        Assert.Equal(new long[] { 0, 1000 }, Offsets(cursor.AdvanceTo(1000)));

        // 边录边剪：录制继续增长时追加新弹幕，含一条比当前位置更早的乱序数据。
        cursor.Append(new[] { Item(2000, "late"), Item(500, "inserted") });

        Assert.Equal(4, cursor.Count);
        // 当前位置之前的 500 不回放（已显示内容不重复），当前位置之后正常推送。
        Assert.Empty(Offsets(cursor.AdvanceTo(1500)));
        Assert.Equal(new long[] { 2000 }, Offsets(cursor.AdvanceTo(2500)));
        Assert.Empty(Offsets(cursor.AdvanceTo(3000)));
    }

    [Fact]
    public void Append_before_playback_starts_from_beginning()
    {
        var cursor = new DanmakuPlaybackCursor(Array.Empty<RecordedDanmakuItem>());

        cursor.Append(new[] { Item(300, "b"), Item(100, "a") });

        Assert.Equal(2, cursor.Count);
        Assert.Equal(new long[] { 100, 300 }, Offsets(cursor.AdvanceTo(400)));
    }

    [Fact]
    public void Append_null_or_empty_is_noop()
    {
        var cursor = new DanmakuPlaybackCursor(new[] { Item(100, "a") });
        cursor.AdvanceTo(200);

        cursor.Append(null);
        cursor.Append(Array.Empty<RecordedDanmakuItem>());

        Assert.Equal(1, cursor.Count);
        Assert.Empty(cursor.AdvanceTo(300));
    }
}
