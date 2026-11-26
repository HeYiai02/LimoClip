using LiveMonitor.Core.Models;
using LiveMonitor.Plugins.Huya;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaProviderParsingTests
{
    private static HuyaStreamLine Line(string cdn, int lineIndex, int priority, string url = "https://hs.flv.huya.com/src/1.flv")
        => new(cdn, lineIndex, priority, url);

    private static HuyaRoomSnapshot Snapshot(
        IReadOnlyList<HuyaStreamLine>? lines = null,
        IReadOnlyList<HuyaBitRateInfo>? bitRates = null,
        int maxBitRate = 20000)
        => new(
            RoomId: "660000",
            PresenterUid: 1,
            Nick: "虎牙赛事",
            RoomName: "S16",
            IsLive: true,
            FlvLines: lines ?? new[] { Line("HS", 2, 45) },
            BitRates: bitRates ?? Array.Empty<HuyaBitRateInfo>(),
            MaxBitRate: maxBitRate);

    // ---------- NormalizeRoomToken ----------

    [Fact]
    public void NormalizeRoomToken_NumericRoomId_ReturnsAsIs()
        => Assert.Equal("660000", HuyaProviderParsing.NormalizeRoomToken("660000"));

    [Fact]
    public void NormalizeRoomToken_RoomIdWithWhitespace_IsTrimmed()
        => Assert.Equal("660000", HuyaProviderParsing.NormalizeRoomToken("  660000  "));

    [Fact]
    public void NormalizeRoomToken_HuyaLink_ExtractsRoomId()
        => Assert.Equal("660000", HuyaProviderParsing.NormalizeRoomToken("https://www.huya.com/660000"));

    [Fact]
    public void NormalizeRoomToken_PersonalHostName_ReturnsHost()
        => Assert.Equal("lpl", HuyaProviderParsing.NormalizeRoomToken("lpl"));

    [Fact]
    public void NormalizeRoomToken_UnparseableValue_FallsBackToTrimmedInput()
        => Assert.Equal(
            "https://live.bilibili.com/1",
            HuyaProviderParsing.NormalizeRoomToken("https://live.bilibili.com/1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeRoomToken_Blank_Throws(string? roomId)
    {
        var ex = Assert.Throws<ArgumentException>(() => HuyaProviderParsing.NormalizeRoomToken(roomId!));
        Assert.Equal("roomId", ex.ParamName);
    }

    // ---------- StabilityRank ----------

    [Theory]
    [InlineData("HS", 0)]
    [InlineData(" hs ", 0)]
    [InlineData("AL", 1)]
    [InlineData("TX", 2)]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("UNKNOWN", 3)]
    public void StabilityRank_MapsCdnType(string? cdn, int expected)
        => Assert.Equal(expected, HuyaProviderParsing.StabilityRank(cdn));

    // ---------- SelectStableLine ----------

    [Fact]
    public void SelectStableLine_PrefersHsEvenWithLowerPriority()
    {
        var lines = new[]
        {
            Line("AL", 0, 100, "https://al.flv.huya.com/1.flv"),
            Line("TX", 1, 90, "https://tx.flv.huya.com/1.flv"),
            Line("HS", 2, 1, "https://hs.flv.huya.com/1.flv"),
        };

        var selected = HuyaProviderParsing.SelectStableLine(lines);

        Assert.Equal("https://hs.flv.huya.com/1.flv", selected.Url);
    }

    [Fact]
    public void SelectStableLine_SameRank_PrefersHigherWebPriorityRate()
    {
        var lines = new[]
        {
            Line("AL", 0, 10, "https://al-low.flv.huya.com/1.flv"),
            Line("AL", 1, 90, "https://al-high.flv.huya.com/1.flv"),
        };

        var selected = HuyaProviderParsing.SelectStableLine(lines);

        Assert.Equal("https://al-high.flv.huya.com/1.flv", selected.Url);
    }

    [Fact]
    public void SelectStableLine_FullTie_PrefersLowerLineIndex()
    {
        var lines = new[]
        {
            Line("AL", 5, 50, "https://later.flv.huya.com/1.flv"),
            Line("AL", 1, 50, "https://earlier.flv.huya.com/1.flv"),
        };

        var selected = HuyaProviderParsing.SelectStableLine(lines);

        Assert.Equal("https://earlier.flv.huya.com/1.flv", selected.Url);
    }

    // ---------- NormalizeRatio ----------

    [Theory]
    [InlineData(500, 500)]
    [InlineData(2000, 2000)]
    [InlineData(4000, 4000)]
    [InlineData(8000, 8000)]
    [InlineData(20000, 20000)]
    [InlineData(50000, 20000)]
    public void NormalizeRatio_KnownBitRates_MapToSupportedRatios(int bitRate, int expected)
        => Assert.Equal(expected, HuyaProviderParsing.NormalizeRatio(bitRate));

    [Theory]
    [InlineData(0, 20000)]
    [InlineData(499, 20000)]
    [InlineData(501, 20000)]
    [InlineData(1500, 20000)]
    [InlineData(1999, 20000)]
    public void NormalizeRatio_UnsupportedBitRates_FallBackTo20000(int bitRate, int expected)
        => Assert.Equal(expected, HuyaProviderParsing.NormalizeRatio(bitRate));

    [Theory]
    [InlineData(3000, 2000)]
    [InlineData(3999, 2000)]
    [InlineData(7999, 4000)]
    [InlineData(19999, 8000)]
    public void NormalizeRatio_RoundsDownToLowerTier(int bitRate, int expected)
        => Assert.Equal(expected, HuyaProviderParsing.NormalizeRatio(bitRate));

    // ---------- HeightForRatio ----------

    [Theory]
    [InlineData(500, 450)]
    [InlineData(2000, 720)]
    [InlineData(4000, 1080)]
    [InlineData(8000, 1080)]
    [InlineData(20000, 1080)]
    public void HeightForRatio_MapsToResolutionHeight(int ratio, int expected)
        => Assert.Equal(expected, HuyaProviderParsing.HeightForRatio(ratio));

    // ---------- AppendRatio ----------

    [Fact]
    public void AppendRatio_UrlWithoutQuery_AddsQuestionMark()
        => Assert.Equal("https://hs.flv.huya.com/1.flv?ratio=500", HuyaProviderParsing.AppendRatio("https://hs.flv.huya.com/1.flv", 500));

    [Fact]
    public void AppendRatio_UrlWithQuery_AddsAmpersand()
        => Assert.Equal(
            "https://hs.flv.huya.com/1.flv?wsSecret=a&ratio=4000",
            HuyaProviderParsing.AppendRatio("https://hs.flv.huya.com/1.flv?wsSecret=a", 4000));

    // ---------- BuildHeaders ----------

    [Fact]
    public void BuildHeaders_NoCookie_OmitsCookieHeader()
    {
        var headers = HuyaProviderParsing.BuildHeaders("660000", null);

        Assert.Equal(PlatformDefaults.UserAgent, headers["User-Agent"]);
        Assert.Equal("https://www.huya.com/660000", headers["Referer"]);
        Assert.False(headers.ContainsKey("Cookie"));
    }

    [Fact]
    public void BuildHeaders_WhitespaceCookie_OmitsCookieHeader()
        => Assert.False(HuyaProviderParsing.BuildHeaders("660000", "   ").ContainsKey("Cookie"));

    [Fact]
    public void BuildHeaders_CookieWithNewline_IsSanitized()
    {
        var headers = HuyaProviderParsing.BuildHeaders("660000", "a=1\r\nb=2");

        Assert.Equal("a=1  b=2", headers["Cookie"]);
    }

    // ---------- BuildQualities ----------

    [Fact]
    public void BuildQualities_NoLines_ReturnsEmpty()
    {
        var snapshot = Snapshot(lines: Array.Empty<HuyaStreamLine>());

        Assert.Empty(HuyaProviderParsing.BuildQualities(snapshot, null));
    }

    [Fact]
    public void BuildQualities_NoBitRates_GeneratesOriginalQualityAtMaxRatio()
    {
        var snapshot = Snapshot(maxBitRate: 20000);

        var quality = Assert.Single(HuyaProviderParsing.BuildQualities(snapshot, null));
        Assert.Equal("huya-flv-20000", quality.Id);
        Assert.Equal("原画", quality.DisplayName);
        Assert.Equal(1080, quality.Height);
        Assert.Equal(30, quality.Fps);
        Assert.Contains("?ratio=20000", quality.StreamUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQualities_ZeroBitRateEntry_UsesRoomMaxBitRate()
    {
        var snapshot = Snapshot(
            bitRates: new[]
            {
                new HuyaBitRateInfo("蓝光20M", 0),
                new HuyaBitRateInfo("蓝光4M", 4000),
            },
            maxBitRate: 20000);

        var qualities = HuyaProviderParsing.BuildQualities(snapshot, null);

        Assert.Equal(2, qualities.Count);
        Assert.Equal("蓝光20M", qualities[0].DisplayName);
        Assert.Equal("huya-flv-20000", qualities[0].Id);
        Assert.Equal("蓝光4M", qualities[1].DisplayName);
        Assert.Equal(1080, qualities[1].Height);
    }

    [Fact]
    public void BuildQualities_OrdersByRatioDescending()
    {
        var snapshot = Snapshot(
            bitRates: new[]
            {
                new HuyaBitRateInfo("高清", 2000),
                new HuyaBitRateInfo("蓝光", 8000),
                new HuyaBitRateInfo("标清", 500),
            });

        var qualities = HuyaProviderParsing.BuildQualities(snapshot, null);

        Assert.Equal(new[] { 8000, 2000, 500 }, qualities.Select(quality => int.Parse(quality.Id["huya-flv-".Length..])).ToArray());
    }

    [Fact]
    public void BuildQualities_DuplicateRatio_KeepsFirstAfterOrdering()
    {
        var snapshot = Snapshot(
            bitRates: new[]
            {
                new HuyaBitRateInfo("蓝光8M", 8000),
                new HuyaBitRateInfo("蓝光9M", 9000),
            });

        var quality = Assert.Single(HuyaProviderParsing.BuildQualities(snapshot, null));
        Assert.Equal("蓝光8M", quality.DisplayName);
        Assert.Equal("huya-flv-8000", quality.Id);
    }

    [Fact]
    public void BuildQualities_BlankDisplayName_FallsBackToOriginal()
    {
        var snapshot = Snapshot(bitRates: new[] { new HuyaBitRateInfo("  ", 4000) });

        var quality = Assert.Single(HuyaProviderParsing.BuildQualities(snapshot, null));
        Assert.Equal("原画", quality.DisplayName);
    }

    [Fact]
    public void BuildQualities_UsesMostStableLineAndReferer()
    {
        var snapshot = Snapshot(
            lines: new[]
            {
                Line("AL", 0, 100, "https://al.flv.huya.com/1.flv"),
                Line("HS", 1, 1, "https://hs.flv.huya.com/1.flv?wsSecret=abc"),
            },
            bitRates: new[] { new HuyaBitRateInfo("蓝光4M", 4000) });

        var quality = Assert.Single(HuyaProviderParsing.BuildQualities(snapshot, "a=1"));

        Assert.StartsWith("https://hs.flv.huya.com/1.flv?wsSecret=abc&ratio=4000", quality.StreamUrl, StringComparison.Ordinal);
        Assert.Equal("https://www.huya.com/660000", quality.RequiredHeaders["Referer"]);
        Assert.Equal("a=1", quality.RequiredHeaders["Cookie"]);
    }

    [Fact]
    public void BuildQualities_HeadersAreSharedAcrossQualities()
    {
        var snapshot = Snapshot(
            bitRates: new[]
            {
                new HuyaBitRateInfo("蓝光4M", 4000),
                new HuyaBitRateInfo("高清", 2000),
            });

        var qualities = HuyaProviderParsing.BuildQualities(snapshot, null);

        Assert.Equal(2, qualities.Count);
        Assert.Same(qualities[0].RequiredHeaders, qualities[1].RequiredHeaders);
    }
}
