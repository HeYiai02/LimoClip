using System.Text.Json;
using LiveMonitor.Platforms;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// <see cref="BilibiliConnectionParsing"/> 的直接单测：覆盖 wbi 密钥提取、
/// 弹幕服务器地址选择与 WebSocket URI 构造、Cookie 取值与弹幕 uid 解析。
/// </summary>
public sealed class BilibiliConnectionParsingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractWbiKey_BlankInput_ReturnsNull(string? url)
    {
        Assert.Null(BilibiliConnectionParsing.ExtractWbiKey(url));
    }

    [Theory]
    [InlineData("https://i0.hdslb.com/bfs/wbi/7c1d9310a0b3.png", "7c1d9310a0b3")]
    [InlineData("https://i0.hdslb.com/bfs/wbi/abc.js", "abc")]
    [InlineData("  https://i0.hdslb.com/bfs/wbi/abc.png  ", "abc")]
    [InlineData("abc.png", "abc")]
    [InlineData("noext", "noext")]
    [InlineData("https://i0.hdslb.com/bfs/wbi/a.b.c.png", "a")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("https://i0.hdslb.com/bfs/wbi/abc.js?x=1", "abc")]
    public void ExtractWbiKey_UrlShapes_ReturnsFileNameWithoutExtension(string url, string expected)
    {
        Assert.Equal(expected, BilibiliConnectionParsing.ExtractWbiKey(url));
    }

    [Fact]
    public void ExtractWbiKey_TrailingSlash_ReturnsNull()
    {
        Assert.Null(BilibiliConnectionParsing.ExtractWbiKey("https://i0.hdslb.com/bfs/wbi/"));
    }

    [Theory]
    [InlineData("127.0.0.1", 443)]
    [InlineData("wss://127.0.0.1", 443)]
    [InlineData("ws://127.0.0.1", 443)]
    [InlineData("https://127.0.0.1/", 443)]
    [InlineData("http://127.0.0.1", 443)]
    public void TryBuildWebSocketUri_KnownHostShapes_BuildsSubEndpoint(string host, int port)
    {
        Assert.True(BilibiliConnectionParsing.TryBuildWebSocketUri(host, port, out var uri));
        Assert.NotNull(uri);
        Assert.Equal("wss", uri!.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(port, uri.Port);
        Assert.Equal("/sub", uri.AbsolutePath);
    }

    [Theory]
    [InlineData("", 443)]
    [InlineData("   ", 443)]
    [InlineData("bad host", 443)]
    public void TryBuildWebSocketUri_InvalidHost_ReturnsFalse(string host, int port)
    {
        Assert.False(BilibiliConnectionParsing.TryBuildWebSocketUri(host, port, out _));
    }

    [Theory]
    [InlineData("{\"host\":\"a.example.com\",\"wss_port\":443,\"ws_port\":2244,\"port\":2243}", "a.example.com", 443)]
    [InlineData("{\"host\":\"a.example.com\",\"wss_port\":0,\"ws_port\":2244,\"port\":2243}", "a.example.com", 2244)]
    [InlineData("{\"host\":\"a.example.com\",\"wss_port\":0,\"ws_port\":0,\"port\":2243}", "a.example.com", 2243)]
    [InlineData("{\"host\":\"  a.example.com  \",\"wss_port\":443}", "a.example.com", 443)]
    public void SelectHostUri_PrefersWssThenWsThenPlainPort(string json, string expectedHost, int expectedPort)
    {
        using var document = JsonDocument.Parse("[" + json + "]");
        var uri = BilibiliConnectionParsing.SelectHostUri(document.RootElement);

        Assert.NotNull(uri);
        Assert.Equal(expectedHost, uri!.Host);
        Assert.Equal(expectedPort, uri.Port);
        Assert.Equal("/sub", uri.AbsolutePath);
    }

    [Fact]
    public void SelectHostUri_SkipsInvalidAndBlankEntries_AndPicksFirstUsable()
    {
        const string json = """
            [
              "not-an-object",
              { "host": "   ", "wss_port": 443 },
              { "host": "", "wss_port": 443 },
              { "host": "first.example.com", "wss_port": 0, "ws_port": 0, "port": 0 },
              { "host": "second.example.com", "wss_port": 443 }
            ]
            """;
        using var document = JsonDocument.Parse(json);
        var uri = BilibiliConnectionParsing.SelectHostUri(document.RootElement);

        Assert.NotNull(uri);
        Assert.Equal("second.example.com", uri!.Host);
    }

    [Fact]
    public void SelectHostUri_EmptyArray_ReturnsNull()
    {
        using var document = JsonDocument.Parse("[]");
        Assert.Null(BilibiliConnectionParsing.SelectHostUri(document.RootElement));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetCookieValue_BlankCookie_ReturnsNull(string? cookie)
    {
        Assert.Null(BilibiliConnectionParsing.GetCookieValue(cookie, "buvid3"));
    }

    [Theory]
    [InlineData("buvid3=abc; DedeUserID=42", "buvid3", "abc")]
    [InlineData("buvid3=abc; DedeUserID=42", "DedeUserID", "42")]
    [InlineData("  a = 1 ; b=2 ", "a", "1")]
    [InlineData("buvid3=abc", "missing", null)]
    [InlineData("=x;buvid3=abc", "buvid3", "abc")]
    [InlineData("buvid3=;DedeUserID=42", "buvid3", null)]
    [InlineData("buvid3=first;buvid3=second", "buvid3", "first")]
    [InlineData("garbage;buvid3=abc", "buvid3", "abc")]
    public void GetCookieValue_SegmentShapes_ReturnsDocumentedValue(string cookie, string name, string? expected)
    {
        Assert.Equal(expected, BilibiliConnectionParsing.GetCookieValue(cookie, name));
    }

    [Fact]
    public void GetCookieValue_IsCaseSensitive()
    {
        Assert.Null(BilibiliConnectionParsing.GetCookieValue("dedeuserid=42", "DedeUserID"));
    }

    [Theory]
    [InlineData("buvid3=x; DedeUserID=12345", 12345L)]
    [InlineData("DedeUserID=42", 42L)]
    [InlineData("DedeUserID= 42 ", 42L)]
    [InlineData("DedeUserID=0", 0L)]
    [InlineData("DedeUserID=-5", 0L)]
    [InlineData("DedeUserID=abc", 0L)]
    [InlineData("buvid3=abc", 0L)]
    [InlineData(null, 0L)]
    public void ParseOwnerUid_CookieShapes_ReturnsPositiveUidOrZero(string? cookie, long expected)
    {
        Assert.Equal(expected, BilibiliConnectionParsing.ParseOwnerUid(cookie));
    }
}
