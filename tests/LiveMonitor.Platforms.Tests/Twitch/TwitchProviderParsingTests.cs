using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class TwitchProviderParsingTests
{
    // ---------- ExtractAuthToken ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractAuthToken_BlankCookie_ReturnsNull(string? cookie)
        => Assert.Null(TwitchProviderParsing.ExtractAuthToken(cookie));

    [Fact]
    public void ExtractAuthToken_ReturnsValueAmongOtherSegments()
        => Assert.Equal("abc123", TwitchProviderParsing.ExtractAuthToken("foo=bar; auth-token=abc123; baz=qux"));

    [Fact]
    public void ExtractAuthToken_NameIsCaseInsensitive()
        => Assert.Equal("token-value", TwitchProviderParsing.ExtractAuthToken("AUTH-TOKEN=token-value"));

    [Fact]
    public void ExtractAuthToken_TrimsSurroundingWhitespace()
        => Assert.Equal("abc", TwitchProviderParsing.ExtractAuthToken("auth-token=  abc  "));

    [Fact]
    public void ExtractAuthToken_EmptyValue_ReturnsNull()
        => Assert.Null(TwitchProviderParsing.ExtractAuthToken("auth-token= ; other=1"));

    [Fact]
    public void ExtractAuthToken_SkipsSegmentsWithoutEquals()
        => Assert.Equal("t", TwitchProviderParsing.ExtractAuthToken("broken; auth-token=t"));

    [Fact]
    public void ExtractAuthToken_SkipsEmptyNameSegment()
        => Assert.Equal("other", TwitchProviderParsing.ExtractAuthToken("=value; auth-token=other"));

    [Fact]
    public void ExtractAuthToken_DoesNotMatchLongerName()
        => Assert.Null(TwitchProviderParsing.ExtractAuthToken("auth-token-x=v"));

    [Fact]
    public void ExtractAuthToken_FirstMatchWins()
        => Assert.Equal("first", TwitchProviderParsing.ExtractAuthToken("auth-token=first; auth-token=second"));

    [Fact]
    public void ExtractAuthToken_ValueMayContainEquals()
        => Assert.Equal("a=b=c", TwitchProviderParsing.ExtractAuthToken("auth-token=a=b=c"));

    // ---------- BuildUsherUrl ----------

    [Fact]
    public void BuildUsherUrl_UsesEscapedChannelInPath()
    {
        var url = TwitchProviderParsing.BuildUsherUrl("some channel", "tok", "sig");

        Assert.StartsWith(
            "https://usher.ttvnw.net/api/channel/hls/some%20channel.m3u8?",
            url,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUsherUrl_ContainsRequiredQueryParameters()
    {
        var url = TwitchProviderParsing.BuildUsherUrl("shroud", "tok", "sig");

        Assert.Contains("allow_source=true", url, StringComparison.Ordinal);
        Assert.Contains("allow_audio_only=true", url, StringComparison.Ordinal);
        Assert.Contains("fast_bread=true", url, StringComparison.Ordinal);
        Assert.Contains("player_backend=mediaplayer", url, StringComparison.Ordinal);
        Assert.Contains("playlist_include_framerate=true", url, StringComparison.Ordinal);
        Assert.Contains("reassignments_supported=true", url, StringComparison.Ordinal);
        Assert.Contains("supported_codecs=av1,h265,h264", url, StringComparison.Ordinal);
        Assert.Contains("p=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUsherUrl_EscapesTokenAndSignature()
    {
        var url = TwitchProviderParsing.BuildUsherUrl("shroud", "to&ken", "s ig+");

        Assert.Contains("token=to%26ken", url, StringComparison.Ordinal);
        Assert.Contains("sig=s%20ig%2B", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUsherUrl_NonceIsSevenDigits()
    {
        var url = TwitchProviderParsing.BuildUsherUrl("shroud", "tok", "sig");
        var marker = url.IndexOf("&p=", StringComparison.Ordinal);
        Assert.True(marker > 0);

        var nonce = url[(marker + 3)..url.IndexOf('&', marker + 3)];
        Assert.Equal(7, nonce.Length);
        Assert.True(int.TryParse(nonce, out _));
    }

    // ---------- ParseAttributes ----------

    [Fact]
    public void ParseAttributes_ParsesQuotedNumericAndUnquotedValues()
    {
        var attributes = TwitchProviderParsing.ParseAttributes(
            "BANDWIDTH=6000000,RESOLUTION=\"1920x1080\",CODECS=\"avc1.640028,mp4a.40.2\",FRAME-RATE=60.000");

        Assert.Equal("6000000", attributes["BANDWIDTH"]);
        Assert.Equal("1920x1080", attributes["RESOLUTION"]);
        Assert.Equal("avc1.640028,mp4a.40.2", attributes["CODECS"]);
        Assert.Equal("60.000", attributes["FRAME-RATE"]);
    }

    [Fact]
    public void ParseAttributes_IsCaseInsensitive()
        => Assert.Equal("1", TwitchProviderParsing.ParseAttributes("bandwidth=1")["BANDWIDTH"]);

    [Fact]
    public void ParseAttributes_UnterminatedQuote_TakesRest()
        => Assert.Equal("unclosed", TwitchProviderParsing.ParseAttributes("RESOLUTION=\"unclosed")["RESOLUTION"]);

    [Fact]
    public void ParseAttributes_BlankInput_ReturnsEmpty()
        => Assert.Empty(TwitchProviderParsing.ParseAttributes("  "));

    [Fact]
    public void ParseAttributes_StopsWhenNoMoreEquals()
        => Assert.Single(TwitchProviderParsing.ParseAttributes("BANDWIDTH=1,TRAILING"));

    [Fact]
    public void ParseAttributes_TrimsKeyWhitespaceAndCommas()
        => Assert.Equal("2", TwitchProviderParsing.ParseAttributes(" BANDWIDTH = 2 ").GetValueOrDefault("BANDWIDTH")?.Trim());

    // ---------- ParseResolution ----------

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("1920X1080", 1920, 1080)]
    [InlineData("1279x719", 1279, 719)]
    public void ParseResolution_ValidInput(string value, int width, int height)
        => Assert.Equal((width, height), TwitchProviderParsing.ParseResolution(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1080")]
    [InlineData("abcxdef")]
    [InlineData("1920x")]
    [InlineData("x1080")]
    public void ParseResolution_InvalidInput_ReturnsZero(string? value)
        => Assert.Equal((0, 0), TwitchProviderParsing.ParseResolution(value));

    // ---------- ParseMasterM3u8 ----------

    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-TWITCH-INFO:ORIGIN="https://usher.ttvnw.net",NODE="x"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio_only",NAME="audio_only",DEFAULT=NO,URI="https://video-weaver.example/audio.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION="1920x1080",FRAME-RATE=60.000,CODECS="avc1.640028,mp4a.40.2"
        https://video-weaver.example/1080p60/index-dvr.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION="1280x720",FRAME-RATE=30.000,CODECS="avc1.64001F,mp4a.40.2"
        https://video-weaver.example/720p30/index-dvr.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=9000000,RESOLUTION="1920x1080",FRAME-RATE=60.000,CODECS="avc1.640028,mp4a.40.2"
        https://video-weaver.example/1080p60-high/index-dvr.m3u8
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMasterM3u8_BlankPlaylist_ReturnsEmpty(string? playlist)
        => Assert.Empty(TwitchProviderParsing.ParseMasterM3u8(playlist!, "shroud"));

    [Fact]
    public void ParseMasterM3u8_ParsesVariants()
    {
        var qualities = TwitchProviderParsing.ParseMasterM3u8(MasterPlaylist, "shroud");

        Assert.Equal(2, qualities.Count);
        var best = qualities[0];
        Assert.Equal("twitch-1080p60", best.Id);
        Assert.Equal("1080P60", best.DisplayName);
        Assert.Equal(1080, best.Height);
        Assert.Equal(60, best.Fps);
        Assert.Equal("https://video-weaver.example/1080p60-high/index-dvr.m3u8", best.StreamUrl);
        Assert.Equal(PlatformDefaults.UserAgent, best.RequiredHeaders["User-Agent"]);
        Assert.Equal($"{PlatformDefaults.TwitchBase}/shroud", best.RequiredHeaders["Referer"]);
    }

    [Fact]
    public void ParseMasterM3u8_DeduplicatesSameHeightAndFpsKeepingHighestBandwidth()
    {
        var qualities = TwitchProviderParsing.ParseMasterM3u8(MasterPlaylist, "shroud");

        Assert.Contains(qualities, quality => quality.StreamUrl.Contains("1080p60-high", StringComparison.Ordinal));
        Assert.DoesNotContain(qualities, quality => quality.StreamUrl.EndsWith("/1080p60/index-dvr.m3u8", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseMasterM3u8_SortsByHeightThenFps()
    {
        var qualities = TwitchProviderParsing.ParseMasterM3u8(MasterPlaylist, "shroud");

        Assert.Equal(new[] { 1080, 720 }, qualities.Select(quality => quality.Height).ToArray());
    }

    [Fact]
    public void ParseMasterM3u8_SkipsVariantWithoutResolution()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,FRAME-RATE=30.000
            https://video-weaver.example/unknown/index-dvr.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION="640x360",FRAME-RATE=30.000
            https://video-weaver.example/360p30/index-dvr.m3u8
            """;

        var quality = Assert.Single(TwitchProviderParsing.ParseMasterM3u8(playlist, "shroud"));
        Assert.Equal(360, quality.Height);
    }

    [Fact]
    public void ParseMasterM3u8_SkipsNonHttpUrl()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION="640x360",FRAME-RATE=30.000
            /local/relative.m3u8
            """;

        Assert.Empty(TwitchProviderParsing.ParseMasterM3u8(playlist, "shroud"));
    }

    [Fact]
    public void ParseMasterM3u8_SkipsVariantWithoutFollowingUrl()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION="640x360",FRAME-RATE=30.000
            #EXT-X-ENDLIST
            """;

        Assert.Empty(TwitchProviderParsing.ParseMasterM3u8(playlist, "shroud"));
    }

    [Fact]
    public void ParseMasterM3u8_InvalidFrameRateDefaultsTo30()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION="640x360",FRAME-RATE=fast
            https://video-weaver.example/360p/index-dvr.m3u8
            """;

        var quality = Assert.Single(TwitchProviderParsing.ParseMasterM3u8(playlist, "shroud"));
        Assert.Equal(30, quality.Fps);
        Assert.Equal("twitch-360p30", quality.Id);
        Assert.Equal("360P", quality.DisplayName);
    }

    // ---------- ParseFormats ----------

    private static JsonElement ParseJson(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private const string LiveFormatsJson = """
        {
          "is_live": true,
          "title": "ESL Pro League",
          "uploader": "ESL",
          "formats": [
            { "url": "https://cdn.example/1080.m3u8", "vcodec": "avc1", "height": 1080, "fps": 60, "tbr": 6000,
              "http_headers": { "Referer": "https://www.twitch.tv/", "X-Test": "1", "Blank": "" } },
            { "url": "https://cdn.example/1080-low.m3u8", "vcodec": "avc1", "height": 1080, "fps": 60, "tbr": 1000 },
            { "url": "https://cdn.example/720.m3u8", "vcodec": "avc1", "height": 720, "fps": 30, "tbr": 3000 },
            { "url": "https://cdn.example/audio.m3u8", "vcodec": "none", "height": 0, "fps": 60, "tbr": 128 }
          ]
        }
        """;

    [Fact]
    public void ParseFormats_ParsesLiveFormatsAndKeepsHighestTbr()
    {
        var qualities = TwitchProviderParsing.ParseFormats(ParseJson(LiveFormatsJson), "eslcs");

        Assert.Equal(2, qualities.Count);
        Assert.Equal("https://cdn.example/1080.m3u8", qualities[0].StreamUrl);
        Assert.Equal("https://www.twitch.tv/", qualities[0].RequiredHeaders["Referer"]);
        Assert.Equal("1", qualities[0].RequiredHeaders["X-Test"]);
        Assert.False(qualities[0].RequiredHeaders.ContainsKey("Blank"));
    }

    [Fact]
    public void ParseFormats_AddsDefaultHeadersWhenMissing()
    {
        const string json = """
            { "formats": [ { "url": "https://cdn.example/720.m3u8", "vcodec": "avc1", "height": 720, "tbr": 3000 } ] }
            """;

        var quality = Assert.Single(TwitchProviderParsing.ParseFormats(ParseJson(json), "eslcs"));
        Assert.Equal(PlatformDefaults.UserAgent, quality.RequiredHeaders["User-Agent"]);
        Assert.Equal($"{PlatformDefaults.TwitchBase}/eslcs", quality.RequiredHeaders["Referer"]);
        Assert.Equal(30, quality.Fps);
    }

    [Fact]
    public void ParseFormats_MissingFormats_ReturnsEmpty()
        => Assert.Empty(TwitchProviderParsing.ParseFormats(ParseJson("""{ "is_live": true }"""), "eslcs"));

    [Fact]
    public void ParseFormats_NonArrayFormats_ReturnsEmpty()
        => Assert.Empty(TwitchProviderParsing.ParseFormats(ParseJson("""{ "formats": {} }"""), "eslcs"));

    [Fact]
    public void ParseFormats_FiltersInvalidEntries()
    {
        const string json = """
            {
              "formats": [
                { "vcodec": "avc1", "height": 720 },
                { "url": "ftp://cdn.example/720.m3u8", "vcodec": "avc1", "height": 720 },
                { "url": "https://cdn.example/720.m3u8", "height": 720 },
                { "url": "https://cdn.example/720.m3u8", "vcodec": "none", "height": 720 },
                { "url": "https://cdn.example/720.m3u8", "vcodec": "avc1", "height": 0 }
              ]
            }
            """;

        Assert.Empty(TwitchProviderParsing.ParseFormats(ParseJson(json), "eslcs"));
    }

    [Fact]
    public void ParseFormats_ZeroFpsDefaultsTo30()
    {
        const string json = """
            { "formats": [ { "url": "https://cdn.example/720.m3u8", "vcodec": "avc1", "height": 720, "fps": 0, "tbr": 3000 } ] }
            """;

        var quality = Assert.Single(TwitchProviderParsing.ParseFormats(ParseJson(json), "eslcs"));
        Assert.Equal(30, quality.Fps);
    }

    // ---------- ParseStatus ----------

    [Fact]
    public void ParseStatus_Offline_UsesTitleAndAnchorFallbacks()
    {
        var status = TwitchProviderParsing.ParseStatus(
            "eslcs",
            ParseJson("""{ "is_live": false, "title": "Back later" }"""));

        Assert.False(status.IsLive);
        Assert.Equal(PlatformType.Twitch, status.Platform);
        Assert.Equal("Back later", status.Title);
        Assert.Equal("eslcs", status.AnchorName);
        Assert.Empty(status.AvailableQualities);
    }

    [Fact]
    public void ParseStatus_Live_UsesUploaderAsAnchor()
    {
        var status = TwitchProviderParsing.ParseStatus("eslcs", ParseJson(LiveFormatsJson));

        Assert.True(status.IsLive);
        Assert.Equal("ESL Pro League", status.Title);
        Assert.Equal("ESL", status.AnchorName);
        Assert.Equal(2, status.AvailableQualities.Count);
    }

    [Fact]
    public void ParseStatus_LiveWithoutPlayableFormats_Throws()
    {
        var json = ParseJson("""{ "is_live": true, "formats": [] }""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => TwitchProviderParsing.ParseStatus("eslcs", json));
        Assert.Contains("eslcs", ex.Message, StringComparison.Ordinal);
    }

    // ---------- IsOffline ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unrelated error")]
    public void IsOffline_NonOfflineText_ReturnsFalse(string? stderr)
        => Assert.False(TwitchProviderParsing.IsOffline(stderr!));

    [Theory]
    [InlineData("ERROR: The requested channel is not currently live")]
    [InlineData("channel is offline")]
    [InlineData("ERROR: This channel is offline")]
    [InlineData("not live")]
    public void IsOffline_OfflineText_ReturnsTrue(string stderr)
        => Assert.True(TwitchProviderParsing.IsOffline(stderr));

    // ---------- IsTwitchHost ----------

    [Theory]
    [InlineData("twitch.tv")]
    [InlineData("www.twitch.tv")]
    [InlineData("m.twitch.tv")]
    [InlineData("go.twitch.tv")]
    [InlineData("WWW.TWITCH.TV")]
    public void IsTwitchHost_KnownHosts_ReturnsTrue(string host)
        => Assert.True(TwitchProviderParsing.IsTwitchHost(host));

    [Theory]
    [InlineData("youtube.com")]
    [InlineData("twitch.tv.evil.com")]
    [InlineData("evil-twitch.tv")]
    [InlineData("")]
    public void IsTwitchHost_UnknownHosts_ReturnsFalse(string host)
        => Assert.False(TwitchProviderParsing.IsTwitchHost(host));

    // ---------- TryGetChannelSegment ----------

    [Fact]
    public void TryGetChannelSegment_ChannelUrl_ReturnsUnescapedChannel()
    {
        Assert.True(TwitchProviderParsing.TryGetChannelSegment(
            new Uri("https://www.twitch.tv/ESL_CS/"), out var channel));
        Assert.Equal("ESL_CS", channel);
    }

    [Fact]
    public void TryGetChannelSegment_PercentEncoded_IsUnescaped()
    {
        Assert.True(TwitchProviderParsing.TryGetChannelSegment(
            new Uri("https://www.twitch.tv/a%5Fb"), out var channel));
        Assert.Equal("a_b", channel);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/")]
    [InlineData("https://www.twitch.tv/videos/12345")]
    [InlineData("https://www.twitch.tv/shroud/clips")]
    [InlineData("https://www.twitch.tv/directory/game/Just%20Chatting")]
    public void TryGetChannelSegment_ReservedOrEmptyPaths_ReturnsFalse(string url)
        => Assert.False(TwitchProviderParsing.TryGetChannelSegment(new Uri(url), out _));

    // ---------- IsChannelName ----------

    [Theory]
    [InlineData("shroud")]
    [InlineData("a")]
    [InlineData("A1_b")]
    [InlineData("abcdefghijklmnopqrstuvwx")]
    public void IsChannelName_ValidNames_ReturnsTrue(string value)
        => Assert.True(TwitchProviderParsing.IsChannelName(value));

    [Theory]
    [InlineData("")]
    [InlineData("ab-c")]
    [InlineData("ab.c")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("chinese中文")]
    public void IsChannelName_InvalidNames_ReturnsFalse(string value)
        => Assert.False(TwitchProviderParsing.IsChannelName(value));
}
