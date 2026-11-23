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
}
