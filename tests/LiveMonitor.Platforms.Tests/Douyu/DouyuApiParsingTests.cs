using System.Text.Json;
using LiveMonitor.Core.Models;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuApiParsingTests
{
    private static JsonElement Parse(string json)
    {
        var document = JsonDocument.Parse(json);
        return document.RootElement;
    }

    private static JsonElement Room(JsonElement root) => root.GetProperty("room");

    // ---------- NormalizeCookie ----------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" a=1 ", "a=1")]
    [InlineData("a=1\r\nb=2", "a=1  b=2")]
    [InlineData("a=1\nb=2", "a=1 b=2")]
    public void NormalizeCookie_CleansValue(string? cookie, string? expected)
        => Assert.Equal(expected, DouyuApiParsing.NormalizeCookie(cookie));

    // ---------- ParseRoom ----------

    [Fact]
    public void ParseRoom_Live_ParsesAllFields()
    {
        var room = DouyuApiParsing.ParseRoom(Parse(DouyuTestData.BetardJson(true)), "9999");

        Assert.Equal("9999", room.RoomId);
        Assert.Equal("陪伴每一天！", room.RoomName);
        Assert.Equal("yyfyyf", room.Nick);
        Assert.Equal(204389, room.OwnerUid);
        Assert.True(room.IsLive);
    }

    [Fact]
    public void ParseRoom_NotLive_ReportsNotLive()
    {
        var room = DouyuApiParsing.ParseRoom(Parse(DouyuTestData.BetardJson(false)), "9999");

        Assert.False(room.IsLive);
        Assert.Equal("休息中", room.RoomName);
    }

    [Fact]
    public void ParseRoom_MissingRoomObject_ThrowsWithRoomId()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DouyuApiParsing.ParseRoom(Parse("{}"), "8888"));

        Assert.Contains("8888", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRoom_NonObjectRoom_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DouyuApiParsing.ParseRoom(Parse("{\"room\":\"x\"}"), "8888"));

        Assert.Contains("room", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRoom_MissingNameAndNick_ReturnsEmptyStrings()
    {
        var room = DouyuApiParsing.ParseRoom(Parse("{\"room\":{\"room_id\":1,\"show_status\":1}}"), "9999");

        Assert.Equal(string.Empty, room.RoomName);
        Assert.Equal(string.Empty, room.Nick);
        Assert.True(room.IsLive);
    }

    // ---------- ParseOwnerUid ----------

    [Fact]
    public void ParseOwnerUid_Number_ReturnsValue()
        => Assert.Equal(204389, DouyuApiParsing.ParseOwnerUid(Room(Parse("{\"room\":{\"owner_uid\":204389}}"))));

    [Fact]
    public void ParseOwnerUid_NumericString_ReturnsValue()
        => Assert.Equal(204389, DouyuApiParsing.ParseOwnerUid(Room(Parse("{\"room\":{\"owner_uid\":\"204389\"}}"))));

    [Fact]
    public void ParseOwnerUid_InvalidString_ReturnsZero()
        => Assert.Equal(0, DouyuApiParsing.ParseOwnerUid(Room(Parse("{\"room\":{\"owner_uid\":\"abc\"}}"))));

    [Fact]
    public void ParseOwnerUid_Missing_ReturnsZero()
        => Assert.Equal(0, DouyuApiParsing.ParseOwnerUid(Room(Parse("{\"room\":{}}"))));

    [Fact]
    public void ParseOwnerUid_Boolean_ReturnsZero()
        => Assert.Equal(0, DouyuApiParsing.ParseOwnerUid(Room(Parse("{\"room\":{\"owner_uid\":true}}"))));

    // ---------- ParseRoomId ----------

    [Fact]
    public void ParseRoomId_Numeric_ReturnsDigits()
        => Assert.Equal("9999", DouyuApiParsing.ParseRoomId(Room(Parse("{\"room\":{\"room_id\":9999}}")), "fallback"));

    [Fact]
    public void ParseRoomId_String_ReturnsValue()
        => Assert.Equal("8888", DouyuApiParsing.ParseRoomId(Room(Parse("{\"room\":{\"room_id\":\"8888\"}}")), "fallback"));

    [Fact]
    public void ParseRoomId_Missing_FallsBack()
        => Assert.Equal("fallback", DouyuApiParsing.ParseRoomId(Room(Parse("{\"room\":{}}")), "fallback"));

    [Fact]
    public void ParseRoomId_Null_FallsBack()
        => Assert.Equal("fallback", DouyuApiParsing.ParseRoomId(Room(Parse("{\"room\":{\"room_id\":null}}")), "fallback"));

    // ---------- BuildStreamUrl ----------

    [Fact]
    public void BuildStreamUrl_RelativePath_JoinsWithSlash()
        => Assert.Equal("https://stream.example/live/abc.flv", DouyuApiParsing.BuildStreamUrl("https://stream.example/live/", "abc.flv"));

    [Fact]
    public void BuildStreamUrl_LeadingSlashPath_DoesNotDuplicateSlash()
        => Assert.Equal("https://stream.example/live/abc.flv", DouyuApiParsing.BuildStreamUrl("https://stream.example/live", "/abc.flv"));

    [Fact]
    public void BuildStreamUrl_AbsoluteHttpsPath_ReturnsAsIs()
        => Assert.Equal("https://cdn.example/abc.flv", DouyuApiParsing.BuildStreamUrl("https://stream.example/live", "https://cdn.example/abc.flv"));

    [Fact]
    public void BuildStreamUrl_AbsoluteHttpPath_ReturnsAsIs()
        => Assert.Equal("http://cdn.example/abc.flv", DouyuApiParsing.BuildStreamUrl("https://stream.example/live", "http://cdn.example/abc.flv"));

    [Fact]
    public void BuildStreamUrl_EmptyPath_KeepsTrailingSlash()
        => Assert.Equal("https://stream.example/live/", DouyuApiParsing.BuildStreamUrl("https://stream.example/live", string.Empty));

    // ---------- ResolveQualityName ----------

    [Fact]
    public void ResolveQualityName_MatchingRate_ReturnsName()
        => Assert.Equal("蓝光4M", DouyuApiParsing.ResolveQualityName(CustomData(multiratesJson: "[{\"rate\":0,\"name\":\"原画2K60\"},{\"rate\":4,\"name\":\"蓝光4M\"}]"), 4));

    [Fact]
    public void ResolveQualityName_RateZero_ReturnsFirstEntryName()
        => Assert.Equal("原画2K60", DouyuApiParsing.ResolveQualityName(CustomData(multiratesJson: "[{\"rate\":0,\"name\":\"原画2K60\"}]"), 0));

    [Fact]
    public void ResolveQualityName_NoMatch_ReturnsOriginal()
        => Assert.Equal("原画", DouyuApiParsing.ResolveQualityName(CustomData(multiratesJson: "[{\"rate\":0,\"name\":\"原画2K60\"}]"), 4));

    [Fact]
    public void ResolveQualityName_BlankName_ReturnsOriginal()
        => Assert.Equal("原画", DouyuApiParsing.ResolveQualityName(CustomData(multiratesJson: "[{\"rate\":4,\"name\":\"  \"}]"), 4));

    [Fact]
    public void ResolveQualityName_MissingMultirates_ReturnsOriginal()
        => Assert.Equal("原画", DouyuApiParsing.ResolveQualityName(CustomData(multiratesJson: null), 4));

    [Fact]
    public void ResolveQualityName_NonArrayMultirates_ReturnsOriginal()
        => Assert.Equal("原画", DouyuApiParsing.ResolveQualityName(CustomData(multiratesRaw: "\"x\""), 4));

    // ---------- BuildQuality ----------

    [Fact]
    public void BuildQuality_Live_ParsesQualityAndHeaders()
    {
        var quality = DouyuApiParsing.BuildQuality(Parse(DouyuTestData.StreamJson()), "9999", "a=1");

        Assert.NotNull(quality);
        Assert.Equal("douyu-4", quality!.Id);
        Assert.Equal("蓝光4M", quality.DisplayName);
        Assert.Equal(0, quality.Height);
        Assert.Equal(0, quality.Fps);
        Assert.Equal("https://stream.example/live/abc_4000.flv?wsAuth=x&token=y", quality.StreamUrl);
        Assert.StartsWith("Mozilla/5.0", quality.RequiredHeaders["User-Agent"], StringComparison.Ordinal);
        Assert.Equal("https://www.douyu.com/9999", quality.RequiredHeaders["Referer"]);
        Assert.Equal("a=1", quality.RequiredHeaders["Cookie"]);
    }

    [Fact]
    public void BuildQuality_WithoutCookie_OmitsCookieHeader()
    {
        var quality = DouyuApiParsing.BuildQuality(Parse(DouyuTestData.StreamJson()), "9999", null);

        Assert.NotNull(quality);
        Assert.False(quality!.RequiredHeaders.ContainsKey("Cookie"));
    }

    [Fact]
    public void BuildQuality_ErrorResponse_ThrowsWithCodeAndMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DouyuApiParsing.BuildQuality(Parse(DouyuTestData.StreamJson(error: 5, message: "签名错误")), "9999", null));

        Assert.Contains("error=5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("签名错误", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuality_MissingData_ReturnsNull()
        => Assert.Null(DouyuApiParsing.BuildQuality(Parse("{\"error\":0,\"data\":\"x\"}"), "9999", null));

    [Fact]
    public void BuildQuality_BlankStreamUrl_ReturnsNull()
        => Assert.Null(DouyuApiParsing.BuildQuality(
            Parse("{\"error\":0,\"data\":{\"rtmp_url\":\" \",\"rtmp_live\":\"a.flv\"}}"), "9999", null));

    [Fact]
    public void BuildQuality_BlankLivePath_ReturnsNull()
        => Assert.Null(DouyuApiParsing.BuildQuality(
            Parse("{\"error\":0,\"data\":{\"rtmp_url\":\"https://a\",\"rtmp_live\":\" \"}}"), "9999", null));

    [Fact]
    public void BuildQuality_MissingRate_DefaultsToZero()
    {
        var quality = DouyuApiParsing.BuildQuality(
            Parse("{\"error\":0,\"data\":{\"rtmp_url\":\"https://a\",\"rtmp_live\":\"b.flv\"}}"), "9999", null);

        Assert.NotNull(quality);
        Assert.Equal("douyu-0", quality!.Id);
    }

    // ---------- helpers ----------

    private static JsonElement CustomData(string? multiratesJson = null, string? multiratesRaw = null)
    {
        var multirates = multiratesRaw ?? multiratesJson ?? "[]";
        return Parse($"{{\"multirates\":{multirates}}}");
    }
}
