using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

/// <summary>弹幕渲染层纯逻辑的边界覆盖（轨道/运动/出生/文本）。</summary>
public sealed class DanmakuOverlayLogicTests
{
    private static DanmakuMessage Message(
        string content,
        bool isSticker = false,
        string? emoteName = null,
        PlatformType platform = PlatformType.Bilibili) =>
        new(platform, "room-1", "uid-1", "sender", content, DateTimeOffset.UnixEpoch)
        {
            IsSticker = isSticker,
            Emotes = emoteName is null ? null : new[] { new DanmakuEmote(emoteName) }
        };

    [Theory]
    [InlineData(0, 1, 26)]
    [InlineData(26, 1, 26)]
    [InlineData(48, 1, 36)]
    [InlineData(96, 3, 28)]
    [InlineData(100, 3, 29.3333)]
    [InlineData(1000, 38, 26)]
    public void ComputeLaneMetrics_ComputesLaneCountAndHeight(
        double height,
        int expectedCount,
        double expectedHeight)
    {
        var metrics = DanmakuOverlayLogic.ComputeLaneMetrics(height);

        Assert.Equal(expectedCount, metrics.LaneCount);
        Assert.Equal(expectedHeight, metrics.LaneHeight, 3);
    }

    [Fact]
    public void ComputeLaneMetrics_NegativeHeight_StillOneLane()
    {
        var metrics = DanmakuOverlayLogic.ComputeLaneMetrics(-50);

        Assert.Equal(1, metrics.LaneCount);
        Assert.Equal(26, metrics.LaneHeight, 3);
    }

    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(0.1, 0.25)]
    [InlineData(10.0, 4.0)]
    [InlineData(-1.0, 0.25)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void ClampPlaybackRate_ClampsAndFallsBack(double value, double expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.ClampPlaybackRate(value), 3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(500, 200)]
    public void ClampMaxConcurrent_ClampsToRange(int value, int expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.ClampMaxConcurrent(value));
    }

    [Theory]
    [InlineData(100, 10, 0.5, 95)]
    [InlineData(100, 10, 1, 90)]
    [InlineData(0, 10, 1, -10)]
    [InlineData(100, 0, 1, 100)]
    public void AdvanceX_MovesItemLeft(double x, double speed, double delta, double expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.AdvanceX(x, speed, delta), 6);
    }

    [Theory]
    [InlineData(-30, 29.999, true)]
    [InlineData(-30, 30, false)]
    [InlineData(-10, 5, true)]
    [InlineData(0, 10, false)]
    [InlineData(100, 10, false)]
    public void IsOffscreen_RecyclesOnlyAfterFullyLeaving(double x, double width, bool expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.IsOffscreen(x, width));
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(0.5, 9)]
    [InlineData(1, 11)]
    [InlineData(-1, 7)]
    [InlineData(2, 11)]
    public void DurationFor_MapsSampleToSevenThroughElevenSeconds(double sample, double expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.DurationFor(sample), 6);
    }

    [Theory]
    [InlineData(100, 20, 10, 12)]
    [InlineData(100, 0, 10, 10)]
    [InlineData(100, 20, 0, 0)]
    [InlineData(100, 20, -1, 0)]
    public void SpeedFor_CrossesCanvasWidthPlusItemWidth(
        double canvasWidth,
        double itemWidth,
        double duration,
        double expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.SpeedFor(canvasWidth, itemWidth, duration), 6);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(100, 104)]
    public void SpawnX_StartsJustRightOfCanvas(double canvasWidth, double expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.SpawnX(canvasWidth), 6);
    }

    [Fact]
    public void FormatText_NonSticker_ReturnsRawContent()
    {
        var text = DanmakuOverlayLogic.FormatText(Message("hello world"));

        Assert.Equal("hello world", text);
    }

    [Fact]
    public void FormatText_StickerWithEmoteName_WrapsInBrackets()
    {
        var text = DanmakuOverlayLogic.FormatText(Message("doge", isSticker: true, emoteName: "doge"));

        Assert.Equal("[doge]", text);
    }

    [Fact]
    public void FormatText_StickerWithBracketedEmoteName_TrimsBrackets()
    {
        var text = DanmakuOverlayLogic.FormatText(Message("x", isSticker: true, emoteName: " [doge] "));

        Assert.Equal("[doge]", text);
    }

    [Fact]
    public void FormatText_StickerWithoutEmoteName_FallsBackToContent()
    {
        var text = DanmakuOverlayLogic.FormatText(Message("Kappa", isSticker: true));

        Assert.Equal("[Kappa]", text);
    }

    [Fact]
    public void FormatText_StickerWithoutAnyName_ReturnsRawContent()
    {
        var text = DanmakuOverlayLogic.FormatText(Message("  ", isSticker: true));

        Assert.Equal("  ", text);
    }

    [Theory]
    [InlineData("  hello  ", "hello")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("a\r\nb\tc", "a  b c")]
    public void NormalizeText_TrimsAndFlattensWhitespace(string input, string expected)
    {
        Assert.Equal(expected, DanmakuOverlayLogic.NormalizeText(input));
    }

    [Fact]
    public void NormalizeText_KeepsTextAtLimit()
    {
        var text = new string('x', DanmakuOverlayLogic.MaxTextLength);

        Assert.Equal(text, DanmakuOverlayLogic.NormalizeText(text));
    }

    [Fact]
    public void NormalizeText_TruncatesOverLimitWithEllipsis()
    {
        var text = new string('x', DanmakuOverlayLogic.MaxTextLength + 1);

        var normalized = DanmakuOverlayLogic.NormalizeText(text);

        Assert.Equal(new string('x', DanmakuOverlayLogic.MaxTextLength) + "…", normalized);
    }

    [Fact]
    public void Constants_StayInSyncWithRenderingTuning()
    {
        Assert.Equal(26, DanmakuOverlayLogic.LaneHeight);
        Assert.Equal(6, DanmakuOverlayLogic.LaneTopPadding);
        Assert.Equal(24, DanmakuOverlayLogic.SpawnGap);
        Assert.Equal(600, DanmakuOverlayLogic.MaxPending);
        Assert.Equal(7, DanmakuOverlayLogic.MinDurationSeconds);
        Assert.Equal(11, DanmakuOverlayLogic.MaxDurationSeconds);
        Assert.Equal(22, DanmakuOverlayLogic.EmoteImageHeight);
        Assert.Equal(17, DanmakuOverlayLogic.FontSize);
    }
}
