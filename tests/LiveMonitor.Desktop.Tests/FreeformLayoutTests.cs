using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class FreeformLayoutTests
{
    private const double CanvasWidth = 1600;
    private const double CanvasHeight = 900;

    private static readonly IReadOnlyList<NormalizedRect> NoOthers = Array.Empty<NormalizedRect>();

    private static void AssertClose(double expected, double actual, double tolerance = 1e-6) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Fact]
    public void NormalizedRect_ExposesEdgesAndCenters()
    {
        var rect = new NormalizedRect(0.25, 0.1, 0.5, 0.4);

        AssertClose(0.75, rect.Right);
        AssertClose(0.5, rect.Bottom);
        AssertClose(0.5, rect.CenterX);
        AssertClose(0.3, rect.CenterY);
    }

    [Fact]
    public void NormalizedRect_ClampsInsideCanvasWithMinimumSize()
    {
        var rect = new NormalizedRect(0.9, 0.9, 0.5, 0.5).ClampToCanvas();

        Assert.True(rect.Left >= 0 && rect.Top >= 0);
        Assert.True(rect.Right <= 1.0 + 1e-9 && rect.Bottom <= 1.0 + 1e-9);
        AssertClose(0.5, rect.Width);
        AssertClose(0.5, rect.Height);
    }

    [Fact]
    public void NormalizedRect_FromPixels_MapsToCanvas()
    {
        var rect = NormalizedRect.FromPixels(400, 180, 800, 450, CanvasWidth, CanvasHeight);

        AssertClose(0.25, rect.Left);
        AssertClose(0.2, rect.Top);
        AssertClose(0.5, rect.Width);
        AssertClose(0.5, rect.Height);
    }

    [Fact]
    public void SnapMove_SnapsToCanvasEdge_AndReturnsGuide()
    {
        var result = FreeformSnapEngine.SnapMove(
            new NormalizedRect(0.004, 0.1, 0.3, 0.3),
            NoOthers,
            CanvasWidth,
            CanvasHeight);

        AssertClose(0.0, result.Rect.Left);
        Assert.Single(result.Guides);
        Assert.True(result.Guides[0].IsVertical);
        AssertClose(0.0, result.Guides[0].Position);
        Assert.False(result.Guides[0].IsCenterGuide);
    }

    [Fact]
    public void SnapMove_SnapsToNeighborLeftEdge_Seamlessly()
    {
        var neighbor = new NormalizedRect(0.5, 0.0, 0.5, 1.0);
        var result = FreeformSnapEngine.SnapMove(
            new NormalizedRect(0.497, 0.1, 0.3, 0.3),
            new[] { neighbor },
            CanvasWidth,
            CanvasHeight);

        AssertClose(0.5, result.Rect.Left);
        Assert.Contains(result.Guides, guide => guide.IsVertical && !guide.IsCenterGuide);
    }

    [Fact]
    public void SnapMove_SnapsCenterToCanvasCenter_WithCenterGuide()
    {
        // 宽度 0.4，left=0.297 -> center=0.497，距离 0.5 只有 0.003 (< 8/1600=0.005)。
        var result = FreeformSnapEngine.SnapMove(
            new NormalizedRect(0.297, 0.2, 0.4, 0.4),
            NoOthers,
            CanvasWidth,
            CanvasHeight);

        AssertClose(0.3, result.Rect.Left);
        Assert.Contains(result.Guides, guide => guide.IsVertical && guide.IsCenterGuide && Math.Abs(guide.Position - 0.5) < 1e-9);
    }

    [Fact]
    public void SnapMove_WithBypass_ReturnsNoGuides()
    {
        var result = FreeformSnapEngine.SnapMove(
            new NormalizedRect(0.004, 0.2, 0.3, 0.3),
            NoOthers,
            CanvasWidth,
            CanvasHeight,
            bypassSnap: true);

        AssertClose(0.004, result.Rect.Left);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnapMove_ClampsOutOfBoundsRect()
    {
        var result = FreeformSnapEngine.SnapMove(
            new NormalizedRect(1.5, -0.2, 0.3, 0.3),
            NoOthers,
            CanvasWidth,
            CanvasHeight,
            bypassSnap: true);

        AssertClose(0.7, result.Rect.Left);
        AssertClose(0.0, result.Rect.Top);
    }

    [Fact]
    public void SnapResize_RightEdgeSnapsToCanvasCenter()
    {
        var origin = new NormalizedRect(0.1, 0.1, 0.35, 0.5); // right = 0.45
        var result = FreeformSnapEngine.SnapResize(
            origin,
            ResizeEdge.Right,
            deltaXNorm: 0.047,
            deltaYNorm: 0,
            NoOthers,
            CanvasWidth,
            CanvasHeight);

        AssertClose(0.5, result.Rect.Right);
        Assert.Contains(result.Guides, guide => guide.IsVertical && guide.IsCenterGuide);
    }

    [Fact]
    public void SnapResize_RespectsMinimumWidth()
    {
        var origin = new NormalizedRect(0.1, 0.1, 0.35, 0.5);
        var result = FreeformSnapEngine.SnapResize(
            origin,
            ResizeEdge.Right,
            deltaXNorm: -0.4,
            deltaYNorm: 0,
            NoOthers,
            CanvasWidth,
            CanvasHeight);

        Assert.True(result.Rect.Width >= 180.0 / CanvasWidth - 1e-9);
        Assert.True(result.Rect.Right <= 1.0);
    }

    [Fact]
    public void SnapResize_TopLeftCornerMovesBothEdges()
    {
        var origin = new NormalizedRect(0.1, 0.1, 0.4, 0.4);
        var result = FreeformSnapEngine.SnapResize(
            origin,
            ResizeEdge.TopLeft,
            deltaXNorm: -0.05,
            deltaYNorm: -0.05,
            NoOthers,
            CanvasWidth,
            CanvasHeight);

        AssertClose(0.05, result.Rect.Left);
        AssertClose(0.05, result.Rect.Top);
        Assert.True(result.Rect.Right <= 1.0 && result.Rect.Bottom <= 1.0);
    }

    [Fact]
    public void SnapResize_LockAspect16By9_AdjustsHeightByPixelRatio()
    {
        var origin = new NormalizedRect(0.1, 0.1, 0.2, 0.1);
        var result = FreeformSnapEngine.SnapResize(
            origin,
            ResizeEdge.Right,
            deltaXNorm: 0.1,
            deltaYNorm: 0,
            NoOthers,
            CanvasWidth,
            CanvasHeight,
            lockAspect16By9: true);

        // 宽度 0.3 * 1600 = 480px，16:9 高度 270px，归一化 270/900 = 0.3。
        AssertClose(0.3, result.Rect.Width);
        AssertClose(0.3, result.Rect.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(13)]
    public void AutoFit_TilesCoverCanvasWithoutOverlap(int count)
    {
        var placements = FreeformLayoutPresets.AutoFit(count, 16.0 / 9.0);

        Assert.Equal(count, placements.Count);
        AssertNoOverlap(placements);
        AssertAllInsideCanvas(placements);
        Assert.All(placements, placement => Assert.False(placement.IsPinned));
    }

    [Fact]
    public void AutoFit_FourTilesUseTwoByTwoGrid()
    {
        var placements = FreeformLayoutPresets.AutoFit(4, 16.0 / 9.0);

        AssertClose(0.0, placements[0].Left);
        AssertClose(0.0, placements[0].Top);
        AssertClose(0.5, placements[0].Width);
        AssertClose(0.5, placements[0].Height);
        AssertClose(1.0, placements[3].ToRect().Right);
        AssertClose(1.0, placements[3].ToRect().Bottom);
    }

    [Fact]
    public void Hero_PutsMainTileLeftAndSideTilesInColumn()
    {
        var placements = FreeformLayoutPresets.Hero(5);

        Assert.Equal(5, placements.Count);
        AssertClose(0.72, placements[0].Width);
        AssertClose(1.0, placements[0].Height);
        Assert.All(placements.Skip(1), placement => AssertClose(0.28, placement.Width));

        var sideTop = placements[1].Top;
        AssertClose(0.0, sideTop);
        AssertClose(1.0, placements[^1].ToRect().Bottom);
        AssertNoOverlap(placements);
    }

    [Fact]
    public void PictureInPicture_BackgroundPlusPinnedWindows()
    {
        var placements = FreeformLayoutPresets.PictureInPicture(3);

        Assert.Equal(3, placements.Count);
        AssertClose(1.0, placements[0].Width);
        Assert.Equal(1, placements[0].ZIndex);
        Assert.False(placements[0].IsPinned);

        Assert.All(placements.Skip(1), placement =>
        {
            Assert.True(placement.IsPinned);
            Assert.True(placement.ZIndex >= 1000);
            AssertClose(0.28, placement.Width);
            AssertClose(0.28, placement.Height);
        });
    }

    [Fact]
    public void DefaultLanding_CentersNewTileInsideCanvas()
    {
        var placement = FreeformLayoutPresets.DefaultLanding(0, "pane-7");

        Assert.Equal("pane-7", placement.PaneId);
        AssertClose(0.5, placement.ToRect().CenterX);
        AssertClose(0.5, placement.ToRect().CenterY);
        Assert.True(placement.ToRect().Right <= 1.0 && placement.ToRect().Bottom <= 1.0);
    }

    [Fact]
    public void Document_RoundTripsPlacements()
    {
        var document = FreeformLayoutDocument.FromPlacements(FreeformLayoutPresets.PictureInPicture(3));
        var json = document.Serialize();

        Assert.True(FreeformLayoutDocument.LooksLikeFreeform(json));
        var parsed = FreeformLayoutDocument.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(document.Tiles.Count, parsed!.Tiles.Count);
        Assert.Equal(2, parsed.Version);
        Assert.Equal(FreeformLayoutDocument.FreeformMode, parsed.Mode);
        AssertClose(document.Tiles[1].Left, parsed.Tiles[1].Left);
        Assert.True(parsed.Tiles[1].IsPinned);
        Assert.Equal(document.Tiles[1].ZIndex, parsed.Tiles[1].ZIndex);
    }

    [Fact]
    public void Document_RejectsLegacyDockJson()
    {
        const string legacyDockJson = "{\"dockables\":{\"$\type\":\"Dock.Model.Mvvm.Controls.RootDock\",\"Id\":\"workspace-root\"}}";
        const string emptyJson = "{}";
        const string versionOneJson = "{\"version\":1,\"mode\":\"freeform\",\"tiles\":[]}";

        Assert.False(FreeformLayoutDocument.LooksLikeFreeform(legacyDockJson));
        Assert.Null(FreeformLayoutDocument.TryParse(legacyDockJson));
        Assert.False(FreeformLayoutDocument.LooksLikeFreeform(emptyJson));
        Assert.False(FreeformLayoutDocument.LooksLikeFreeform(versionOneJson));
    }

    [Fact]
    public void Document_ClampsInvalidGeometryOnParse()
    {
        const string json = """
            {
              "version": 2,
              "mode": "freeform",
              "tiles": [
                { "paneId": "pane-1", "left": 2.5, "top": -1.0, "width": 3.0, "height": 2.0, "zIndex": 0 },
                { "paneId": "pane-1", "left": 0.1, "top": 0.1, "width": 0.2, "height": 0.2 },
                { "paneId": "pane-2", "left": 0.1, "top": 0.1, "width": 0.2, "height": 0.2 }
              ]
            }
            """;

        var parsed = FreeformLayoutDocument.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Tiles.Count); // 重复 PaneId 去重
        var first = parsed.Tiles[0].ToRect();
        Assert.True(first.Left >= 0 && first.Top >= 0 && first.Right <= 1.0 && first.Bottom <= 1.0);
        Assert.Equal(1, parsed.Tiles[0].ZIndex); // zIndex 最低归一到 1
    }

    [Fact]
    public void BuildMigratedDocument_MapsSlotRoomsToFreeformGrid()
    {
        var ids = new Guid?[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null };
        var document = FreeformLayoutPresets.BuildMigratedDocument(ids, 16.0 / 9.0);

        Assert.Equal(2, document.Version);
        Assert.Equal(4, document.Tiles.Count);
        Assert.Equal(ids[0], document.Tiles[0].LiveRoomId);
        Assert.Equal(ids[2], document.Tiles[2].LiveRoomId);
        Assert.Null(document.Tiles[3].LiveRoomId);
    }

    private static void AssertAllInsideCanvas(IEnumerable<FreeformTilePlacement> placements)
    {
        foreach (var placement in placements)
        {
            var rect = placement.ToRect();
            Assert.True(rect.Left >= -1e-9, $"{placement.PaneId} left={rect.Left}");
            Assert.True(rect.Top >= -1e-9, $"{placement.PaneId} top={rect.Top}");
            Assert.True(rect.Right <= 1.0 + 1e-9, $"{placement.PaneId} right={rect.Right}");
            Assert.True(rect.Bottom <= 1.0 + 1e-9, $"{placement.PaneId} bottom={rect.Bottom}");
        }
    }

    private static void AssertNoOverlap(IReadOnlyList<FreeformTilePlacement> placements)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            for (var j = i + 1; j < placements.Count; j++)
            {
                Assert.False(
                    placements[i].ToRect().IntersectsWith(placements[j].ToRect(), 1e-6),
                    $"{placements[i].PaneId} 与 {placements[j].PaneId} 重叠");
            }
        }
    }
}
