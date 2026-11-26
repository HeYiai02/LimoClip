using System;
using System.Collections.Generic;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Services;

/// <summary>自由画布磁吸引擎（移动/8 向缩放/最小尺寸/16:9 锁定）的直接单测。</summary>
public sealed class FreeformSnapEngineTests
{
    private static readonly NormalizedRect[] NoOthers = Array.Empty<NormalizedRect>();

    private static NormalizedRect R(double left, double top, double width, double height)
        => new(left, top, width, height);

    [Fact]
    public void SnapMove_NothingWithinThreshold_KeepsRectAndReturnsNoGuides()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.1, 0.1, 0.2, 0.2), NoOthers, 1000, 1000);

        Assert.Equal(R(0.1, 0.1, 0.2, 0.2), result.Rect);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnapMove_LeftEdgeNearCanvas_SnapsToZero()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.003, 0.1, 0.2, 0.2), NoOthers, 1000, 1000);

        Assert.Equal(0.0, result.Rect.Left, 10);
        var guide = Assert.Single(result.Guides);
        Assert.True(guide.IsVertical);
        Assert.Equal(0.0, guide.Position, 10);
        Assert.False(guide.IsCenterGuide);
    }

    [Fact]
    public void SnapMove_RightEdgeNearCanvas_SnapsToRight()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.794, 0.2, 0.2, 0.2), NoOthers, 1000, 1000);

        Assert.Equal(0.8, result.Rect.Left, 10);
        Assert.Equal(1.0, result.Rect.Right, 10);
    }

    [Fact]
    public void SnapMove_CenterNearCanvasCenter_SnapsCenter()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.396, 0.1, 0.2, 0.2), NoOthers, 1000, 1000);

        Assert.Equal(0.4, result.Rect.Left, 10);
        Assert.Contains(result.Guides, guide => guide.IsVertical && guide.IsCenterGuide && Math.Abs(guide.Position - 0.5) < 1e-9);
    }

    [Fact]
    public void SnapMove_SnapsToOtherPaneLeft()
    {
        var others = new[] { R(0.5, 0.2, 0.2, 0.2) };
        var result = FreeformSnapEngine.SnapMove(R(0.497, 0.2, 0.2, 0.2), others, 1000, 1000);

        Assert.Equal(0.5, result.Rect.Left, 10);
        Assert.Contains(result.Guides, guide => guide.IsVertical);
    }

    [Fact]
    public void SnapMove_BypassSnap_OnlyClampsBounds()
    {
        var result = FreeformSnapEngine.SnapMove(R(-0.1, 0.9, 0.2, 0.2), NoOthers, 1000, 1000, bypassSnap: true);

        Assert.Equal(0.0, result.Rect.Left, 10);
        Assert.Equal(0.8, result.Rect.Top, 10);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnapMove_ZeroCanvas_ReturnsClampedWithoutGuides()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.1, 0.1, 0.2, 0.2), NoOthers, 0, 0);

        Assert.Equal(R(0.1, 0.1, 0.2, 0.2), result.Rect);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnapMove_EnforcesPixelMinimumSize()
    {
        var result = FreeformSnapEngine.SnapMove(R(0.1, 0.1, 0.01, 0.01), NoOthers, 1000, 1000);

        Assert.Equal(0.18, result.Rect.Width, 10);
        Assert.Equal(0.1, result.Rect.Height, 10);
    }

    [Fact]
    public void SnapResize_RightEdgeNearCanvasRight_Snaps()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.2, 0.4, 0.4), ResizeEdge.Right, 0.397, 0, NoOthers, 1000, 1000);

        Assert.Equal(1.0, result.Rect.Right, 10);
        var guide = Assert.Single(result.Guides);
        Assert.True(guide.IsVertical);
        Assert.Equal(1.0, guide.Position, 10);
    }

    [Fact]
    public void SnapResize_LeftEdgeSnapsToOtherPaneRight()
    {
        var others = new[] { R(0.2, 0.2, 0.3, 0.4) };
        var result = FreeformSnapEngine.SnapResize(
            R(0.6, 0.2, 0.2, 0.4), ResizeEdge.Left, -0.098, 0, others, 1000, 1000);

        Assert.Equal(0.5, result.Rect.Left, 10);
        Assert.Contains(result.Guides, guide => guide.IsVertical && Math.Abs(guide.Position - 0.5) < 1e-9);
    }

    [Fact]
    public void SnapResize_TopEdgeSnapsToOtherPaneBottom()
    {
        var others = new[] { R(0.2, 0.2, 0.2, 0.2) };
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.6, 0.2, 0.2), ResizeEdge.Top, 0, -0.197, others, 1000, 1000);

        Assert.Equal(0.4, result.Rect.Top, 10);
        Assert.Contains(result.Guides, guide => !guide.IsVertical && Math.Abs(guide.Position - 0.4) < 1e-9);
    }

    [Fact]
    public void SnapResize_ClampsToCanvasRight()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.2, 0.4, 0.4), ResizeEdge.Right, 5, 0, NoOthers, 1000, 1000);

        Assert.Equal(1.0, result.Rect.Right, 10);
        Assert.Equal(0.2, result.Rect.Left, 10);
    }

    [Fact]
    public void SnapResize_LeftEdgeRespectsMinimumWidth()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.2, 0.4, 0.4), ResizeEdge.Left, 0.39, 0, NoOthers, 1000, 1000, bypassSnap: true);

        Assert.Equal(0.42, result.Rect.Left, 10);
        Assert.Equal(0.6, result.Rect.Right, 10);
    }

    [Fact]
    public void SnapResize_BypassSnap_ReturnsNoGuides()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.2, 0.4, 0.4), ResizeEdge.Bottom, 0, 0.002, NoOthers, 1000, 1000, bypassSnap: true);

        Assert.Equal(0.602, result.Rect.Bottom, 10);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnapResize_LockAspect16By9_DerivesHeightFromPixelWidth()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.0, 0.0, 0.4, 0.3), ResizeEdge.Bottom, 0, 0, NoOthers, 1600, 900,
            bypassSnap: true, lockAspect16By9: true);

        Assert.Equal(0.4, result.Rect.Height, 10);
    }

    [Fact]
    public void SnapResize_LockAspect16By9_WithTopEdgeMovesTop()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.0, 0.3, 0.4, 0.2), ResizeEdge.Top, 0, 0, NoOthers, 1600, 900,
            bypassSnap: true, lockAspect16By9: true);

        Assert.Equal(0.4, result.Rect.Height, 10);
        Assert.Equal(0.1, result.Rect.Top, 10);
    }

    [Fact]
    public void SnapResize_ZeroCanvas_ReturnsNoGuides()
    {
        var result = FreeformSnapEngine.SnapResize(
            R(0.2, 0.2, 0.4, 0.4), ResizeEdge.Right, 0, 0, NoOthers, 0, 0);

        Assert.Empty(result.Guides);
        Assert.Equal(0.2, result.Rect.Left, 10);
        Assert.Equal(0.2, result.Rect.Top, 10);
        Assert.Equal(0.4, result.Rect.Width, 10);
        Assert.Equal(0.4, result.Rect.Height, 10);
    }
}
