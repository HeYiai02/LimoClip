using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LiveMonitor.Plugins.Huya;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaApiClientParsingTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    private static byte[] Deflate(string text)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            deflate.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    private static byte[] Brotli(string text)
    {
        using var buffer = new MemoryStream();
        using (var brotliStream = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            brotliStream.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    [Theory]
    [InlineData("{\"status\":200}", 200)]
    [InlineData("{\"status\":422}", 422)]
    [InlineData("{\"status\":0}", 0)]
    [InlineData("{}", 0)]
    [InlineData("{\"status\":\"200\"}", 0)]
    [InlineData("{\"status\":200.5}", 0)]
    public void ReadStatus_ReadsNumberOrFallsBackToZero(string json, int expected)
        => Assert.Equal(expected, HuyaApiClientParsing.ReadStatus(Json(json)));

    [Theory]
    [InlineData("{\"message\":\"ok\"}", "ok")]
    [InlineData("{}", "")]
    [InlineData("{\"message\":42}", "")]
    [InlineData("{\"message\":null}", "")]
    public void ReadMessage_ReadsStringOrFallsBackToEmpty(string json, string expected)
        => Assert.Equal(expected, HuyaApiClientParsing.ReadMessage(Json(json)));

    [Fact]
    public void ReadStatusAndMessage_UndefinedElement_ReturnDefaults()
    {
        Assert.Equal(0, HuyaApiClientParsing.ReadStatus(default));
        Assert.Equal(string.Empty, HuyaApiClientParsing.ReadMessage(default));
    }

    [Theory]
    [InlineData("x=\"profileRoom\":660000,y", "660000")]
    [InlineData("{\"profileRoom\":123456}", "123456")]
    [InlineData("\"profileRoom\" :  42", "42")]
    [InlineData("\"profileRoom\":\n\t7", "7")]
    public void ExtractProfileRoom_ReturnsDigits(string html, string expected)
        => Assert.Equal(expected, HuyaApiClientParsing.ExtractProfileRoom(html));

    [Theory]
    [InlineData("no room here")]
    [InlineData("\"profileRoom\":abc")]
    [InlineData("profileRoom:123")]
    public void ExtractProfileRoom_NoMatch_ReturnsNull(string html)
        => Assert.Null(HuyaApiClientParsing.ExtractProfileRoom(html));

    [Fact]
    public void ExtractProfileRoom_EmptyInput_ReturnsNull()
    {
        Assert.Null(HuyaApiClientParsing.ExtractProfileRoom(null));
        Assert.Null(HuyaApiClientParsing.ExtractProfileRoom(string.Empty));
    }

    [Fact]
    public void ExtractProfileRoom_MultipleMatches_ReturnsFirst()
        => Assert.Equal("1", HuyaApiClientParsing.ExtractProfileRoom("\"profileRoom\":1 \"profileRoom\":2"));

    [Fact]
    public void ReadString_ReadsStringProperty()
        => Assert.Equal("虎牙", HuyaApiClientParsing.ReadString(Json("{\"name\":\"虎牙\"}"), "name"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":42}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":true}")]
    public void ReadString_MissingOrWrongKind_ReturnsEmpty(string json)
        => Assert.Equal(string.Empty, HuyaApiClientParsing.ReadString(Json(json), "name"));

    [Fact]
    public void ReadString_UndefinedElement_ReturnsEmpty()
        => Assert.Equal(string.Empty, HuyaApiClientParsing.ReadString(default, "name"));

    [Theory]
    [InlineData("{\"uid\":123}", 123L)]
    [InlineData("{\"uid\":-5}", -5L)]
    [InlineData("{}", 0L)]
    [InlineData("{\"uid\":\"123\"}", 0L)]
    [InlineData("{\"uid\":12.5}", 0L)]
    [InlineData("{\"uid\":true}", 0L)]
    public void ReadInt64_ReadsIntegerOrFallsBackToZero(string json, long expected)
        => Assert.Equal(expected, HuyaApiClientParsing.ReadInt64(Json(json), "uid"));

    [Fact]
    public void ReadInt64_UndefinedElement_ReturnsZero()
        => Assert.Equal(0L, HuyaApiClientParsing.ReadInt64(default, "uid"));

    [Fact]
    public void ParseFlvLines_ReadsLines()
    {
        var data = Json("""
            {"stream":{"flv":{"multiLine":[
              {"cdnType":"HS","lineIndex":1,"webPriorityRate":100,"url":"https://hs.flv.huya.com/a.flv"},
              {"cdnType":"AL","lineIndex":3,"webPriorityRate":50,"url":"https://al.flv.huya.com/b.flv"}
            ]}}}
            """);

        var lines = HuyaApiClientParsing.ParseFlvLines(data);

        Assert.Equal(2, lines.Count);
        Assert.Equal("HS", lines[0].CdnType);
        Assert.Equal(1, lines[0].LineIndex);
        Assert.Equal(100, lines[0].WebPriorityRate);
        Assert.Equal("https://hs.flv.huya.com/a.flv", lines[0].Url);
        Assert.Equal("AL", lines[1].CdnType);
        Assert.Equal(3, lines[1].LineIndex);
        Assert.Equal(50, lines[1].WebPriorityRate);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"stream\":null}")]
    [InlineData("{\"stream\":{}}")]
    [InlineData("{\"stream\":{\"flv\":{}}}")]
    [InlineData("{\"stream\":{\"flv\":{\"multiLine\":{}}}}")]
    [InlineData("{\"stream\":{\"flv\":{\"multiLine\":[]}}}")]
    [InlineData("{\"stream\":\"not-an-object\"}")]
    public void ParseFlvLines_MissingOrWrongShape_ReturnsEmpty(string json)
        => Assert.Empty(HuyaApiClientParsing.ParseFlvLines(Json(json)));

    [Fact]
    public void ParseFlvLines_SkipsItemsWithoutUrlAndNonObjects()
    {
        var data = Json("""
            {"stream":{"flv":{"multiLine":[
              "not-an-object",
              {"cdnType":"HS","lineIndex":1,"webPriorityRate":100},
              {"cdnType":"AL","lineIndex":2,"webPriorityRate":10,"url":"   "},
              {"url":"https://tx.flv.huya.com/c.flv"}
            ]}}}
            """);

        var line = Assert.Single(HuyaApiClientParsing.ParseFlvLines(data));
        Assert.Equal("https://tx.flv.huya.com/c.flv", line.Url);
        Assert.Equal(string.Empty, line.CdnType);
        Assert.Equal(0, line.LineIndex);
        Assert.Equal(0, line.WebPriorityRate);
    }

    [Fact]
    public void ParseBitRates_ReadsItems()
    {
        var liveData = Json("""
            {"bitRateInfo":"[{\"sDisplayName\":\"蓝光20M\",\"iBitRate\":20000},{\"sDisplayName\":\"高清\",\"iBitRate\":4000}]"}
            """);

        var bitRates = HuyaApiClientParsing.ParseBitRates(liveData);

        Assert.Equal(2, bitRates.Count);
        Assert.Equal("蓝光20M", bitRates[0].DisplayName);
        Assert.Equal(20000, bitRates[0].BitRate);
        Assert.Equal("高清", bitRates[1].DisplayName);
        Assert.Equal(4000, bitRates[1].BitRate);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"bitRateInfo\":\"\"}")]
    [InlineData("{\"bitRateInfo\":\"   \"}")]
    [InlineData("{\"bitRateInfo\":42}")]
    [InlineData("{\"bitRateInfo\":\"not-json\"}")]
    [InlineData("{\"bitRateInfo\":\"{}\"}")]
    [InlineData("{\"bitRateInfo\":\"[]\"}")]
    public void ParseBitRates_MissingOrInvalid_ReturnsEmpty(string json)
        => Assert.Empty(HuyaApiClientParsing.ParseBitRates(Json(json)));

    [Fact]
    public void ParseBitRates_SkipsItemsWithoutDisplayName()
    {
        var liveData = Json("""
            {"bitRateInfo":"[{\"iBitRate\":1},{\"sDisplayName\":\"  \",\"iBitRate\":2},{\"sDisplayName\":\"原画\",\"iBitRate\":20000}]"}
            """);

        var item = Assert.Single(HuyaApiClientParsing.ParseBitRates(liveData));
        Assert.Equal("原画", item.DisplayName);
        Assert.Equal(20000, item.BitRate);
    }

    [Fact]
    public void ParseBitRates_SkipsNonObjectItems()
    {
        var liveData = Json("""
            {"bitRateInfo":"[1,\"x\",{\"sDisplayName\":\"流畅\",\"iBitRate\":500}]"}
            """);

        Assert.Equal("流畅", Assert.Single(HuyaApiClientParsing.ParseBitRates(liveData)).DisplayName);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("GZIP")]
    [InlineData(" gzip ")]
    public void DecodeContent_GzipHeader_Decompresses(string encoding)
        => Assert.Equal("虎牙", HuyaApiClientParsing.DecodeContent(Gzip("虎牙"), new[] { encoding }));

    [Fact]
    public void DecodeContent_GzipMagicWithoutHeader_Decompresses()
        => Assert.Equal("huya", HuyaApiClientParsing.DecodeContent(Gzip("huya"), Array.Empty<string>()));

    [Fact]
    public void DecodeContent_DeflateHeader_Decompresses()
        => Assert.Equal("虎牙", HuyaApiClientParsing.DecodeContent(Deflate("虎牙"), new[] { "deflate" }));

    [Fact]
    public void DecodeContent_BrotliHeader_Decompresses()
        => Assert.Equal("虎牙", HuyaApiClientParsing.DecodeContent(Brotli("虎牙"), new[] { "br" }));

    [Fact]
    public void DecodeContent_PlainUtf8_ReturnsText()
        => Assert.Equal("{\"profileRoom\":1}", HuyaApiClientParsing.DecodeContent(Encoding.UTF8.GetBytes("{\"profileRoom\":1}"), Array.Empty<string>()));

    [Fact]
    public void DecodeContent_UnknownEncoding_ReturnsUtf8Text()
        => Assert.Equal("虎牙", HuyaApiClientParsing.DecodeContent(Encoding.UTF8.GetBytes("虎牙"), new[] { "identity" }));

    [Fact]
    public void DecodeContent_EmptyBytes_ReturnsEmpty()
        => Assert.Equal(string.Empty, HuyaApiClientParsing.DecodeContent(Array.Empty<byte>(), new[] { "gzip" }));
}
