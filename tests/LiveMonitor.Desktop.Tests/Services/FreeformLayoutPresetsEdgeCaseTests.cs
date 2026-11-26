using System;
using System.Linq;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Services;

public sealed class FreeformLayoutPresetsEdgeCaseTests
{
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2 })]
    [InlineData(3, new[] { 1, 2 })]
    [InlineData(4, new[] { 2, 2 })]
    [InlineData(5, new[] { 3, 2 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(7, new[] { 3, 2, 2 })]
    [InlineData(8, new[] { 3, 3, 2 })]
    [InlineData(9, new[] { 3, 3, 3 })]
    [InlineData(10, new[] { 4, 3, 3 })]
    [InlineData(11, new[] { 4, 4, 3 })]
    [InlineData(12, new[] { 4, 4, 4 })]
    public void CalculateRowCounts_matches_legacy_tile_distribution(int count, int[] expected)
    {
        Assert.Equal(expected, FreeformLayoutPresets.CalculateRowCounts(count, 16.0 / 9.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void CalculateRowCounts_treats_non_positive_count_as_single_row(int count)
    {
        Assert.Equal(new[] { 1 }, FreeformLayoutPresets.CalculateRowCounts(count, 16.0 / 9.0));
    }

    [Theory]
    [InlineData(13, 16.0 / 9.0)]
    [InlineData(14, 16.0 / 9.0)]
    [InlineData(16, 16.0 / 9.0)]
    [InlineData(20, 16.0 / 9.0)]
    [InlineData(13, 9.0 / 16.0)]
    [InlineData(17, 21.0 / 9.0)]
    public void CalculateRowCounts_overflow_rows_cover_every_pane(int count, double aspectRatio)
    {
        var rows = FreeformLayoutPresets.CalculateRowCounts(count, aspectRatio);

        Assert.Equal(count, rows.Sum());
        Assert.All(rows, columns => Assert.True(columns >= 1));
        Assert.True(rows.Length >= 1);
    }

    [Fact]
    public void CalculateRowCounts_tall_canvas_uses_more_rows_than_wide_canvas()
    {
        var wide = FreeformLayoutPresets.CalculateRowCounts(18, 16.0 / 9.0);
        var tall = FreeformLayoutPresets.CalculateRowCounts(18, 9.0 / 16.0);

        Assert.True(tall.Length >= wide.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1)]
    public void AutoFit_non_positive_count_yields_single_full_canvas_tile(int count)
    {
        var placements = FreeformLayoutPresets.AutoFit(count, 16.0 / 9.0);

        var only = Assert.Single(placements);
        Assert.Equal("pane-1", only.PaneId);
        Assert.Equal(1.0, only.Width, 10);
        Assert.Equal(1.0, only.Height, 10);
        Assert.Equal(1, only.ZIndex);
    }

    [Fact]
    public void AutoFit_assigns_incremental_z_index_and_pane_ids()
    {
        var placements = FreeformLayoutPresets.AutoFit(5, 16.0 / 9.0);

        Assert.Equal(new[] { "pane-1", "pane-2", "pane-3", "pane-4", "pane-5" },
            placements.Select(placement => placement.PaneId).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, placements.Select(placement => placement.ZIndex).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Hero_single_or_non_positive_count_keeps_only_main_tile(int count)
    {
        var placements = FreeformLayoutPresets.Hero(count);

        var only = Assert.Single(placements);
        Assert.Equal("pane-1", only.PaneId);
        Assert.Equal(1.0, only.Height, 10);
        Assert.Equal(0.72, only.Width, 10);
    }

    [Theory]
    [InlineData(0.1, 0.3)]
    [InlineData(0.0, 0.3)]
    [InlineData(2.0, 0.95)]
    public void Hero_clamps_hero_proportion(double requested, double expectedWidth)
    {
        var placements = FreeformLayoutPresets.Hero(3, requested);

        Assert.Equal(expectedWidth, placements[0].Width, 10);
        Assert.Equal(1.0 - expectedWidth, placements[1].Width, 10);
    }

    [Fact]
    public void Hero_side_tiles_are_stacked_without_gaps()
    {
        var placements = FreeformLayoutPresets.Hero(4, 0.6);

        for (var index = 1; index < placements.Count; index++)
        {
            Assert.Equal(0.6, placements[index].Left, 10);
            Assert.Equal((index - 1) / 3.0, placements[index].Top, 10);
        }

        Assert.Equal(1.0, placements[^1].Top + placements[^1].Height, 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PictureInPicture_single_or_non_positive_count_yields_background_only(int count)
    {
        var placements = FreeformLayoutPresets.PictureInPicture(count);

        var only = Assert.Single(placements);
        Assert.Equal(NormalizedRect.FullCanvas.Left, only.Left, 10);
        Assert.Equal(NormalizedRect.FullCanvas.Width, only.Width, 10);
        Assert.False(only.IsPinned);
        Assert.Equal(1, only.ZIndex);
    }

    [Fact]
    public void PictureInPicture_pinned_tiles_stay_inside_canvas_and_are_topmost()
    {
        var placements = FreeformLayoutPresets.PictureInPicture(6, 0.3);

        Assert.All(placements.Skip(1), placement =>
        {
            var rect = placement.ToRect();
            Assert.True(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= 1.0 + 1e-9 && rect.Bottom <= 1.0 + 1e-9);
            Assert.True(placement.IsPinned);
            Assert.True(placement.ZIndex > placements[0].ZIndex);
        });
        Assert.Equal(1001, placements[1].ZIndex);
    }

    [Theory]
    [InlineData(0.01, 0.12)]
    [InlineData(0.9, 0.5)]
    public void PictureInPicture_clamps_pip_size(double requested, double expected)
    {
        var placements = FreeformLayoutPresets.PictureInPicture(2, requested);

        Assert.Equal(expected, placements[1].Width, 10);
        Assert.Equal(expected, placements[1].Height, 10);
    }

    [Fact]
    public void PictureInPicture_overflow_wraps_to_second_column()
    {
        var placements = FreeformLayoutPresets.PictureInPicture(6);

        Assert.Equal(6, placements.Count);
        Assert.All(placements.Skip(1), placement => Assert.True(placement.ToRect().Right <= 1.0 + 1e-9));
    }

    [Theory]
    [InlineData(0, 0.325)]
    [InlineData(1, 0.355)]
    [InlineData(2, 0.385)]
    [InlineData(4, 0.445)]
    [InlineData(5, 0.325)]
    public void DefaultLanding_cascades_by_existing_tile_count(int existingCount, double expectedLeft)
    {
        var placement = FreeformLayoutPresets.DefaultLanding(existingCount, "pane-9");

        Assert.Equal(expectedLeft, placement.Left, 10);
        Assert.Equal(expectedLeft, placement.Top, 10);
        Assert.Equal(FreeformLayoutPresets.DefaultLandingSize, placement.Width, 10);
    }

    [Fact]
    public void DefaultLanding_keeps_pane_id_and_live_room()
    {
        var roomId = Guid.NewGuid();

        var placement = FreeformLayoutPresets.DefaultLanding(1, "pane-3", roomId);

        Assert.Equal("pane-3", placement.PaneId);
        Assert.Equal(roomId, placement.LiveRoomId);
        var rect = placement.ToRect();
        Assert.True(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= 1.0 + 1e-9 && rect.Bottom <= 1.0 + 1e-9);
    }

    [Fact]
    public void DefaultLanding_clamps_cascade_near_canvas_edge()
    {
        var placement = FreeformLayoutPresets.DefaultLanding(4, "pane-1");

        Assert.True(placement.Left + placement.Width <= 1.0 + 1e-9);
        Assert.True(placement.Top + placement.Height <= 1.0 + 1e-9);
    }

    [Fact]
    public void BuildMigratedDocument_empty_room_list_still_yields_one_tile()
    {
        var document = FreeformLayoutPresets.BuildMigratedDocument(Array.Empty<Guid?>(), 16.0 / 9.0);

        var tile = Assert.Single(document.Tiles);
        Assert.Null(tile.LiveRoomId);
    }

    [Fact]
    public void BuildMigratedDocument_preserves_room_order_and_nulls()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var document = FreeformLayoutPresets.BuildMigratedDocument(
            new Guid?[] { first, null, second },
            16.0 / 9.0);

        Assert.Equal(3, document.Tiles.Count);
        Assert.Equal(first, document.Tiles[0].LiveRoomId);
        Assert.Null(document.Tiles[1].LiveRoomId);
        Assert.Equal(second, document.Tiles[2].LiveRoomId);
    }
}
