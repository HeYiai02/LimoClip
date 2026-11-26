using Avalonia;
using Avalonia.Input;
using LiveMonitor.Desktop.Controls;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

/// <summary>自由画布交互几何逻辑的边界覆盖（热区/阈值/光标/HUD/落位/16:9）。</summary>
public sealed class FreeformInteractionLogicTests
{
    private static NormalizedRect HalfRect() => new(0.25, 0.25, 0.5, 0.5);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]
    [InlineData(2.9, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(0, 3, true)]
    [InlineData(-3, 0, true)]
    [InlineData(0, -3, true)]
    [InlineData(2.13, 2.13, true)]
    public void ExceedsDragThreshold_UsesSquaredDistance(double dx, double dy, bool expected)
    {
        Assert.Equal(expected, FreeformInteractionLogic.ExceedsDragThreshold(dx, dy));
    }

    [Theory]
    [InlineData(400, 300, ResizeEdge.None)]
    [InlineData(200, 300, ResizeEdge.Left)]
    [InlineData(600, 300, ResizeEdge.Right)]
    [InlineData(400, 150, ResizeEdge.Top)]
    [InlineData(400, 450, ResizeEdge.Bottom)]
    [InlineData(200, 150, ResizeEdge.TopLeft)]
    [InlineData(600, 150, ResizeEdge.TopRight)]
    [InlineData(200, 450, ResizeEdge.BottomLeft)]
    [InlineData(600, 450, ResizeEdge.BottomRight)]
    [InlineData(1000, 300, ResizeEdge.None)]
    [InlineData(206, 300, ResizeEdge.Left)]
    [InlineData(206, 150, ResizeEdge.TopLeft)]
    public void HitTestResizeEdge_DetectsEdgesCornersAndOutside(
        double x,
        double y,
        ResizeEdge expected)
    {
        var edge = FreeformInteractionLogic.HitTestResizeEdge(HalfRect(), 800, 600, x, y);

        Assert.Equal(expected, edge);
    }

    [Fact]
    public void HitTestResizeEdge_CornerWinsOverSingleEdge()
    {
        // 角落热区内同时命中左边与上边，应返回角落（用于对角缩放）。
        var edge = FreeformInteractionLogic.HitTestResizeEdge(HalfRect(), 800, 600, 205, 155);

        Assert.Equal(ResizeEdge.TopLeft, edge);
    }

    [Theory]
    [InlineData(ResizeEdge.Left, StandardCursorType.SizeWestEast)]
    [InlineData(ResizeEdge.Right, StandardCursorType.SizeWestEast)]
    [InlineData(ResizeEdge.Top, StandardCursorType.SizeNorthSouth)]
    [InlineData(ResizeEdge.Bottom, StandardCursorType.SizeNorthSouth)]
    [InlineData(ResizeEdge.TopLeft, StandardCursorType.TopLeftCorner)]
    [InlineData(ResizeEdge.BottomRight, StandardCursorType.TopLeftCorner)]
    [InlineData(ResizeEdge.TopRight, StandardCursorType.TopRightCorner)]
    [InlineData(ResizeEdge.BottomLeft, StandardCursorType.TopRightCorner)]
    [InlineData(ResizeEdge.None, StandardCursorType.SizeAll)]
    public void MapResizeCursor_ResizeEdge_ReturnsDirectionalCursor(
        ResizeEdge edge,
        StandardCursorType expected)
    {
        Assert.Equal(expected, FreeformInteractionLogic.MapResizeCursor(edge, moving: false));
    }

    [Theory]
    [InlineData(ResizeEdge.Left)]
    [InlineData(ResizeEdge.TopRight)]
    [InlineData(ResizeEdge.None)]
    public void MapResizeCursor_Moving_AlwaysSizeAll(ResizeEdge edge)
    {
        Assert.Equal(
            StandardCursorType.SizeAll,
            FreeformInteractionLogic.MapResizeCursor(edge, moving: true));
    }

    [Fact]
    public void BuildDragHud_FormatsPixelsAndPercentages()
    {
        var text = FreeformInteractionLogic.BuildDragHud(
            new NormalizedRect(0.25, 0.5, 0.5, 0.25),
            canvasWidth: 800,
            canvasHeight: 600,
            snapped: false);

        Assert.Equal("400 × 150 px · 50% × 25%", text);
    }

    [Fact]
    public void BuildDragHud_AppendsSnapHintWhenSnapped()
    {
        var text = FreeformInteractionLogic.BuildDragHud(
            new NormalizedRect(0, 0, 0.5, 0.5),
            canvasWidth: 800,
            canvasHeight: 600,
            snapped: true);

        Assert.Equal("400 × 300 px · 50% × 50% · 已吸附", text);
    }

    [Fact]
    public void ComputeDropRect_CentersDropOnPointer()
    {
        var rect = FreeformInteractionLogic.ComputeDropRect(400, 300, 800, 600);

        Assert.Equal(0.29, rect.Left, 3);
        Assert.Equal(0.29, rect.Top, 3);
        Assert.Equal(0.42, rect.Width, 3);
        Assert.Equal(0.42, rect.Height, 3);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(800, 600, 0.58, 0.58)]
    [InlineData(100, 550, 0, 0.58)]
    public void ComputeDropRect_ClampsInsideCanvas(
        double x,
        double y,
        double expectedLeft,
        double expectedTop)
    {
        var rect = FreeformInteractionLogic.ComputeDropRect(x, y, 800, 600);

        Assert.Equal(expectedLeft, rect.Left, 3);
        Assert.Equal(expectedTop, rect.Top, 3);
    }

    [Fact]
    public void ComputeDropRect_DegenerateCanvas_UsesFallbackRect()
    {
        var rect = FreeformInteractionLogic.ComputeDropRect(10, 10, 0, 0);

        Assert.Equal(0.3, rect.Left, 3);
        Assert.Equal(0.3, rect.Top, 3);
        Assert.Equal(0.42, rect.Width, 3);
        Assert.Equal(0.42, rect.Height, 3);
    }

    [Fact]
    public void FitTo16By9_KeepsTopWhenRoomAvailable()
    {
        var fitted = FreeformInteractionLogic.FitTo16By9(
            new NormalizedRect(0.1, 0.1, 0.5, 0.3),
            canvasWidth: 800,
            canvasHeight: 450);

        Assert.Equal(0.1, fitted.Top, 3);
        Assert.Equal(0.5, fitted.Height, 3);
    }

    [Fact]
    public void FitTo16By9_ShiftsUpWhenOverflowingBottom()
    {
        var fitted = FreeformInteractionLogic.FitTo16By9(
            new NormalizedRect(0.1, 0.8, 0.5, 0.3),
            canvasWidth: 800,
            canvasHeight: 450);

        Assert.Equal(0.5, fitted.Top, 3);
        Assert.Equal(0.5, fitted.Height, 3);
    }

    [Fact]
    public void FitTo16By9_ClampsToMinimumHeight()
    {
        var fitted = FreeformInteractionLogic.FitTo16By9(
            new NormalizedRect(0.1, 0.1, 0.01, 0.3),
            canvasWidth: 800,
            canvasHeight: 600);

        Assert.Equal(0.05, fitted.Height, 3);
    }

    [Fact]
    public void FitTo16By9_DegenerateCanvas_ReturnsInput()
    {
        var rect = new NormalizedRect(0.1, 0.1, 0.5, 0.3);

        var fitted = FreeformInteractionLogic.FitTo16By9(rect, 800, 0);

        Assert.Equal(rect, fitted);
    }

    [Theory]
    [InlineData(800, 600, 4.0 / 3.0)]
    [InlineData(1600, 900, 16.0 / 9.0)]
    public void AspectOf_ReturnsCanvasRatio(double width, double height, double expected)
    {
        Assert.Equal(expected, FreeformInteractionLogic.AspectOf(width, height), 6);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-800, 600)]
    public void AspectOf_UnknownSize_FallsBackTo16By9(double width, double height)
    {
        Assert.Equal(16.0 / 9.0, FreeformInteractionLogic.AspectOf(width, height), 6);
    }
}
