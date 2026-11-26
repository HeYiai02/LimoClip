using System.Text.Json;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// <see cref="BilibiliProviderParsing"/> 的直接单测：房间号/链接解析、画质描述、
/// 播放地址拼接、画质高度映射与短链域名判定。
/// </summary>
public sealed class BilibiliProviderParsingTests
{
    [Theory]
    [InlineData("123", true)]
    [InlineData("007", true)]
    [InlineData("9223372036854775807", true)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("12a", false)]
    [InlineData(" 123", false)]
    [InlineData("999999999999999999999", false)]
    public void IsNumericId_Shapes(string value, bool expected)
    {
        Assert.Equal(expected, BilibiliProviderParsing.IsNumericId(value));
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("007", "7")]
    public void NormalizeNumeric_ValidInput_ReturnsCanonical(string value, string expected)
    {
        Assert.Equal(expected, BilibiliProviderParsing.NormalizeNumeric(value, "roomUrl"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    public void NormalizeNumeric_InvalidInput_Throws(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => BilibiliProviderParsing.NormalizeNumeric(value, "roomUrl"));
        Assert.Contains("无效的 B 站房间号", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?room_id=123", "room_id", "123")]
    [InlineData("?roomid=456", "roomid", "456")]
    [InlineData("a=1&room_id=789&b=2", "room_id", "789")]
    [InlineData("room_id=1", "room_id", "1")]
    [InlineData("?room_id=hello%20world", "room_id", "hello world")]
    [InlineData("?ROOM_ID=9", "room_id", "9")]
    [InlineData("?room_id=", "room_id", "")]
    [InlineData("?room_id", "room_id", null)]
    [InlineData("?=1", "", null)]
    [InlineData("", "room_id", null)]
    public void ParseQuery_Shapes(string query, string name, string? expected)
    {
        Assert.Equal(expected, BilibiliProviderParsing.ParseQuery(query, name));
    }

    [Theory]
    [InlineData("https://live.bilibili.com/1234", "1234")]
    [InlineData("https://live.bilibili.com/1234?spm=abc", "1234")]
    [InlineData("https://live.bilibili.com/h5/1234", "1234")]
    [InlineData("https://www.bilibili.com/blackboard/live/1234", "1234")]
    [InlineData("https://bilibili.com/1234.html", "1234")]
    [InlineData("https://live.bilibili.com/room_id=1234", null)]
    [InlineData("https://live.bilibili.com/", null)]
    [InlineData("https://live.bilibili.com/abc", null)]
    [InlineData("https://example.com/1234", null)]
    public void ParseRoomId_LinkShapes(string url, string? expected)
    {
        var uri = new Uri(url);
        Assert.Equal(expected, BilibiliProviderParsing.ParseRoomId(uri));
    }

    [Fact]
    public void ParseRoomId_QueryTakesPriorityOverPath()
    {
        var uri = new Uri("https://live.bilibili.com/1234?room_id=999");
        Assert.Equal("999", BilibiliProviderParsing.ParseRoomId(uri));
    }

    [Theory]
    [InlineData("1080P", 15000, 1080)]
    [InlineData("720p60", 250, 720)]
    [InlineData("(1080P 60帧)", 0, 1080)]
    [InlineData("蓝光 20M", 20000, 1440)]
    [InlineData("4K", 30000, 2160)]
    [InlineData(null, 250, 720)]
    [InlineData("", 80, 360)]
    [InlineData("abc", 400, 1080)]
    [InlineData("1080P", 0, 1080)]
    [InlineData("unknown", 1, 0)]
    public void ParseHeight_DescriptionAndQnFallback(string? description, int qn, int expected)
    {
        Assert.Equal(expected, BilibiliProviderParsing.ParseHeight(description, qn));
    }

    [Theory]
    [InlineData("https://cdn.example.com/live.flv", "https://cdn.example.com/live.flv")]
    [InlineData("not-a-url", null)]
    public void BuildStreamUrl_DirectUrl(string directUrl, string? expected)
    {
        var candidate = new BilibiliProviderParsing.Candidate(0, 15000, null, null, null, directUrl);
        Assert.Equal(expected, BilibiliProviderParsing.BuildStreamUrl(candidate));
    }

    [Fact]
    public void BuildStreamUrl_HostPlusRelativeBase_Joins()
    {
        var candidate = new BilibiliProviderParsing.Candidate(0, 15000, "https://a.example.com/", "/live/x.flv", null, null);
        Assert.Equal("https://a.example.com/live/x.flv", BilibiliProviderParsing.BuildStreamUrl(candidate));
    }

    [Fact]
    public void BuildStreamUrl_AbsoluteBase_WinsOverHost()
    {
        var candidate = new BilibiliProviderParsing.Candidate(0, 15000, "https://a.example.com", "https://cdn.example.com/x.flv", null, null);
        Assert.Equal("https://cdn.example.com/x.flv", BilibiliProviderParsing.BuildStreamUrl(candidate));
    }

    [Theory]
    [InlineData("https://a.example.com/live/x.flv", "k=v", "https://a.example.com/live/x.flv?k=v")]
    [InlineData("https://a.example.com/live/x.flv?t=1", "&k=v", "https://a.example.com/live/x.flv?t=1&k=v")]
    [InlineData("https://a.example.com/live/x.flv?", "k=v", "https://a.example.com/live/x.flv?k=v")]
    [InlineData("https://a.example.com/live/x.flv", "&k=v", "https://a.example.com/live/x.flv?k=v")]
    public void BuildStreamUrl_ExtraQuery_Appended(string baseUrl, string extra, string expected)
    {
        var candidate = new BilibiliProviderParsing.Candidate(0, 15000, "https://a.example.com", baseUrl, extra, null);
        Assert.Equal(expected, BilibiliProviderParsing.BuildStreamUrl(candidate));
    }

    [Fact]
    public void BuildStreamUrl_MissingHostAndBase_ReturnsNull()
    {
        var candidate = new BilibiliProviderParsing.Candidate(0, 15000, null, null, "k=v", null);
        Assert.Null(BilibiliProviderParsing.BuildStreamUrl(candidate));
    }

    [Fact]
    public void ParseDescriptions_ReadsBriefAndMediaDetail()
    {
        const string json = """
            {
              "g_qn_desc": [
                { "qn": 15000, "desc": "1080P" },
                { "qn": 20000, "desc": "原画", "media_base_desc": { "brief_desc": { "desc": "蓝光" }, "detail_desc": { "desc": "蓝光 20M" } } },
                { "qn": 0, "desc": "ignored" }
              ]
            }
            """;
        using var document = JsonDocument.Parse(json);
        var result = BilibiliProviderParsing.ParseDescriptions(document.RootElement);

        Assert.Equal(2, result.Count);
        Assert.Equal("1080P", result[15000].Brief);
        Assert.Null(result[15000].Detail);
        Assert.Equal("蓝光", result[20000].Brief);
        Assert.Equal("蓝光 20M", result[20000].Detail);
    }

    [Fact]
    public void ParseDescriptions_MissingOrInvalidArray_ReturnsEmpty()
    {
        using var missing = JsonDocument.Parse("{}");
        Assert.Empty(BilibiliProviderParsing.ParseDescriptions(missing.RootElement));

        using var notArray = JsonDocument.Parse("""{ "g_qn_desc": {} }""");
        Assert.Empty(BilibiliProviderParsing.ParseDescriptions(notArray.RootElement));
    }

    [Theory]
    [InlineData("b23.tv", true)]
    [InlineData("www.b23.tv", true)]
    [InlineData("bili2233.cn", true)]
    [InlineData("x.bili2233.cn", true)]
    [InlineData("live.bilibili.com", false)]
    [InlineData("notb23.tv", false)]
    [InlineData("b23.tv.evil.com", false)]
    public void IsShortLinkHost_Shapes(string host, bool expected)
    {
        Assert.Equal(expected, BilibiliProviderParsing.IsShortLinkHost(host));
    }
}
