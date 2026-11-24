using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class WorkspaceDockBuilderTests
{
    private const double WideAspect = 16.0 / 9.0;

    private static RoomTileViewModel Tile(string paneId) => new(paneId, null);

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    [InlineData(7, 3, 3)]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 4)]
    [InlineData(12, 3, 4)]
    [InlineData(30, 5, 6)]
    public void CalculateAutoFitGrid_ReturnsExpectedGrid(int count, int rows, int columns)
    {
        var result = WorkspaceDockBuilder.CalculateAutoFitGrid(count, WideAspect);

        Assert.Equal((rows, columns), result);
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2 })]
    [InlineData(3, new[] { 1, 2 })]
    [InlineData(4, new[] { 2, 2 })]
    [InlineData(5, new[] { 3, 2 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(7, new[] { 3, 2, 2 })]
    [InlineData(8, new[] { 3, 3, 2 })]
    [InlineData(10, new[] { 4, 3, 3 })]
    public void CreateAutoFit_UsesExpectedRowDistribution(int count, int[] expectedRows)
    {
        var builder = new WorkspaceDockBuilder();

        var layout = builder.CreateAutoFit(count, WideAspect, Tile);

        Assert.Equal(expectedRows, RowItemCounts(layout));
    }

    [Fact]
    public void CreateAutoFit_ThirtyRooms_ProducesThirtyToolDocks()
    {
        var builder = new WorkspaceDockBuilder();

        var layout = builder.CreateAutoFit(30, WideAspect, Tile);

        Assert.Equal(30, EnumerateToolDocks(layout).Count());
        Assert.Equal((5, 6), WorkspaceDockBuilder.GetGridDimensions(layout));
    }

    [Fact]
    public void TryApplyProportions_CopiesGridAndPaneProportions()
    {
        var builder = new WorkspaceDockBuilder();
        var source = builder.CreateGrid(2, 2, Tile);
        var sourceRows = Rows(source).ToList();
        sourceRows[0].Proportion = 0.65;
        sourceRows[1].Proportion = 0.35;
        var sourcePanes = EnumerateToolDocks(source).ToList();
        sourcePanes[0].Proportion = 0.7;
        sourcePanes[1].Proportion = 0.3;

        var target = builder.CreateGrid(2, 2, Tile);
        Assert.True(WorkspaceDockBuilder.TryApplyProportions(target, source));

        var targetRows = Rows(target).ToList();
        Assert.Equal(0.65, targetRows[0].Proportion, 3);
        Assert.Equal(0.35, targetRows[1].Proportion, 3);
        var targetPanes = EnumerateToolDocks(target).ToList();
        Assert.Equal(0.7, targetPanes[0].Proportion, 3);
        Assert.Equal(0.3, targetPanes[1].Proportion, 3);
    }

    [Fact]
    public void ResetProportions_MakesGridAndPanesUniform()
    {
        var builder = new WorkspaceDockBuilder();
        var layout = builder.CreateGrid(2, 2, Tile);
        var rows = Rows(layout).ToList();
        rows[0].Proportion = 0.65;
        rows[1].Proportion = 0.35;
        var panes = EnumerateToolDocks(layout).ToList();
        panes[0].Proportion = 0.7;
        panes[1].Proportion = 0.3;

        Assert.True(WorkspaceDockBuilder.ResetProportions(layout));

        Assert.All(rows, row => Assert.Equal(1, row.Proportion, 3));
        Assert.All(panes, pane => Assert.Equal(1, pane.Proportion, 3));
    }

    [Fact]
    public void TryApplyProportions_RejectsDifferentPaneStructure()
    {
        var builder = new WorkspaceDockBuilder();
        var source = builder.CreateGrid(1, 3, Tile);
        var target = builder.CreateGrid(1, 2, Tile);

        Assert.False(WorkspaceDockBuilder.TryApplyProportions(target, source));
    }

    [Fact]
    public void CloneStructure_PreservesProportionsAndPaneIds()
    {
        var builder = new WorkspaceDockBuilder();
        var source = builder.CreateGrid(1, 2, Tile);
        var sourceRow = FirstRow(source);
        var sourcePanes = sourceRow.VisibleDockables!.OfType<ToolDock>().ToList();
        sourcePanes[0].Proportion = 0.7;
        sourcePanes[1].Proportion = 0.3;

        var clone = builder.CloneStructure(source, Tile);

        var cloneRow = FirstRow(clone);
        var clonePanes = cloneRow.VisibleDockables!.OfType<ToolDock>().ToList();
        Assert.Equal(0.7, clonePanes[0].Proportion, 3);
        Assert.Equal(0.3, clonePanes[1].Proportion, 3);
        Assert.Equal(new[] { "dock-pane-1", "dock-pane-2" }, clonePanes.Select(d => d.Id));
        Assert.Single(cloneRow.VisibleDockables!.OfType<IProportionalDockSplitter>());
    }

    [Fact]
    public void CloneStructure_WithExtraTile_AppendsToLastRow()
    {
        var builder = new WorkspaceDockBuilder();
        var source = builder.CreateGrid(1, 2, Tile);

        var clone = builder.CloneStructure(source, Tile, new RoomTileViewModel("pane-3", null));

        var row = FirstRow(clone);
        var panes = row.VisibleDockables!.OfType<ToolDock>().ToList();
        Assert.Equal(3, panes.Count);
        Assert.Equal("pane-3", panes[^1].ActiveDockable!.Id);
        Assert.Equal(2, row.VisibleDockables!.OfType<IProportionalDockSplitter>().Count());
    }

    [Fact]
    public void CreateHeroAndSideColumn_ThreeRooms_ProducesHeroPlusSideColumn()
    {
        var builder = new WorkspaceDockBuilder();

        var layout = builder.CreateHeroAndSideColumn(3, Tile);

        Assert.Equal("workspace-root", layout.Id);
        var outer = Assert.IsType<ProportionalDock>(layout.VisibleDockables![0]);
        Assert.Equal("workspace-hero", outer.Id);
        Assert.Equal(Dock.Model.Core.Orientation.Horizontal, outer.Orientation);
        Assert.Equal(3, outer.VisibleDockables!.Count);

        var hero = Assert.IsType<ToolDock>(outer.VisibleDockables![0]);
        Assert.Equal(0.7, hero.Proportion, 3);
        Assert.Equal("pane-1", hero.ActiveDockable!.Id);

        Assert.IsType<ProportionalDockSplitter>(outer.VisibleDockables[1]);

        var side = Assert.IsType<ProportionalDock>(outer.VisibleDockables[2]);
        Assert.Equal("workspace-side-column", side.Id);
        Assert.Equal(Dock.Model.Core.Orientation.Vertical, side.Orientation);
        Assert.Equal(0.3, side.Proportion, 3);
        Assert.Equal(new[] { "pane-2", "pane-3" }, SidePaneIds(side));
        Assert.Single(side.VisibleDockables!.OfType<IProportionalDockSplitter>());

        Assert.True(WorkspaceDockBuilder.ContainsDockId(layout, "workspace-hero"));
    }

    [Fact]
    public void CreateHeroAndSideColumn_FewerThanThreeRooms_FallsBackToSingleRow()
    {
        var builder = new WorkspaceDockBuilder();

        var layout = builder.CreateHeroAndSideColumn(2, Tile);

        Assert.False(WorkspaceDockBuilder.ContainsDockId(layout, "workspace-hero"));
        Assert.Equal(new[] { 2 }, RowItemCounts(layout));
    }

    [Fact]
    public void CreateHeroAndSideColumn_HeroProportionIsClamped()
    {
        var builder = new WorkspaceDockBuilder();

        var layout = builder.CreateHeroAndSideColumn(4, Tile, heroProportion: 5.0);

        var outer = Assert.IsType<ProportionalDock>(layout.VisibleDockables![0]);
        var hero = Assert.IsType<ToolDock>(outer.VisibleDockables![0]);
        var side = Assert.IsType<ProportionalDock>(outer.VisibleDockables[2]);
        Assert.Equal(0.85, hero.Proportion, 3);
        Assert.Equal(0.15000000000000002, side.Proportion, 3);
    }

    private static string[] SidePaneIds(ProportionalDock side)
        => side.VisibleDockables!
            .OfType<ToolDock>()
            .Select(dock => dock.ActiveDockable!.Id)
            .ToArray();

    private static ProportionalDock FirstRow(IRootDock layout)
    {
        var outer = (ProportionalDock)layout.VisibleDockables![0];
        return (ProportionalDock)outer.VisibleDockables![0];
    }

    private static IEnumerable<ProportionalDock> Rows(IRootDock layout)
    {
        var outer = (ProportionalDock)layout.VisibleDockables![0];
        return outer.VisibleDockables!.OfType<ProportionalDock>();
    }

    private static int[] RowItemCounts(IRootDock layout)
    {
        var outer = (ProportionalDock)layout.VisibleDockables![0];
        return outer.VisibleDockables!
            .OfType<ProportionalDock>()
            .Select(row => row.VisibleDockables?.OfType<ToolDock>().Count() ?? 0)
            .ToArray();
    }

    private static IEnumerable<ToolDock> EnumerateToolDocks(IRootDock layout)
    {
        var outer = (ProportionalDock)layout.VisibleDockables![0];
        foreach (var row in outer.VisibleDockables!.OfType<ProportionalDock>())
        {
            foreach (var child in row.VisibleDockables ?? Array.Empty<IDockable>())
            {
                if (child is ToolDock toolDock)
                {
                    yield return toolDock;
                }
            }
        }
    }
}
