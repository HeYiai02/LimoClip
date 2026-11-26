using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

public sealed class PlayerChromeLogicTests
{
    [Theory]
    [InlineData(true, "\uE74F")]
    [InlineData(false, "\uE767")]
    public void MuteIcon_UsesFluentGlyph(bool muted, string expected)
        => Assert.Equal(expected, PlayerChromeLogic.MuteIcon(muted));

    [Theory]
    [InlineData(true, "开声")]
    [InlineData(false, "静音")]
    public void MuteTip_TellsNextAction(bool muted, string expected)
        => Assert.Equal(expected, PlayerChromeLogic.MuteTip(muted));

    [Theory]
    [InlineData(true, "15fps")]
    [InlineData(false, "60fps")]
    public void CompactButtonText_ShowsCurrentTier(bool compact, string expected)
        => Assert.Equal(expected, PlayerChromeLogic.CompactButtonText(compact));

    [Theory]
    [InlineData(true, 15)]
    [InlineData(false, 30)]
    public void MaxConcurrentFor_LimitsCompactMode(bool compact, int expected)
        => Assert.Equal(expected, PlayerChromeLogic.MaxConcurrentFor(compact));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void DanmakuLayerVisible_RequiresBothSwitches(
        bool masterEnabled,
        bool tileDanmakuVisible,
        bool expected)
        => Assert.Equal(
            expected,
            PlayerChromeLogic.DanmakuLayerVisible(masterEnabled, tileDanmakuVisible));

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void ClampVolume_ClampsToRange(int input, int expected)
        => Assert.Equal(expected, PlayerChromeLogic.ClampVolume(input));

    [Fact]
    public void Constants_MatchOverlayContract()
    {
        Assert.Equal(15, PlayerChromeLogic.CompactMaxConcurrent);
        Assert.Equal(30, PlayerChromeLogic.NormalMaxConcurrent);
        Assert.Equal(0, PlayerChromeLogic.MinVolume);
        Assert.Equal(100, PlayerChromeLogic.MaxVolume);
    }
}
