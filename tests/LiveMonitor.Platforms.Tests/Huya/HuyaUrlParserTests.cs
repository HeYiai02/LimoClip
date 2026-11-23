using LiveMonitor.Plugins.Huya;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Huya;

public sealed class HuyaUrlParserTests
{
    [Theory]
    [InlineData("https://www.huya.com/660000")]
    [InlineData("https://huya.com/660000")]
    [InlineData("https://m.huya.com/660000")]
    [InlineData("https://www.huya.com/lpl")]
    [InlineData("https://www.huya.com/index.html?roomid=660000")]
    public void CanHandle_AcceptsHuyaLinks(string url)
    {
        Assert.True(HuyaUrlParser.CanHandle(url));
    }

    [Theory]
    [InlineData("660000")]
    [InlineData("lpl")]
    [InlineData("https://live.bilibili.com/123")]
    [InlineData("https://www.youtube.com/watch?v=abcd1234567")]
    [InlineData("not a url")]
    [InlineData("https://www.huya.com.cn/660000")]
    public void CanHandle_RejectsOthers(string url)
    {
        Assert.False(HuyaUrlParser.CanHandle(url));
    }

    [Theory]
    [InlineData("https://www.huya.com/660000", "660000")]
    [InlineData("https://m.huya.com/660000", "660000")]
    [InlineData("https://www.huya.com/lpl", "lpl")]
    [InlineData("https://www.huya.com/index.html?roomid=660000", "660000")]
    [InlineData("https://www.huya.com/?roomid=660000", "660000")]
    public void ExtractRoomId_ReturnsRoomToken(string url, string expected)
    {
        Assert.Equal(expected, HuyaUrlParser.ExtractRoomId(url));
    }

    [Fact]
    public void ExtractRoomId_NumericToken_ReturnsAsIs()
    {
        Assert.Equal("660000", HuyaUrlParser.ExtractRoomId("660000"));
    }

    [Fact]
    public void ExtractRoomId_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => HuyaUrlParser.ExtractRoomId(" "));
    }

    [Fact]
    public void ExtractRoomId_UnsupportedUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => HuyaUrlParser.ExtractRoomId("https://live.bilibili.com/123"));
    }

    [Fact]
    public void ExtractRoomId_LinkWithoutPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => HuyaUrlParser.ExtractRoomId("https://www.huya.com/"));
    }

    [Fact]
    public void IsRoomNumber_DetectsNumericOnly()
    {
        Assert.True(HuyaUrlParser.IsRoomNumber("660000"));
        Assert.False(HuyaUrlParser.IsRoomNumber("lpl"));
        Assert.False(HuyaUrlParser.IsRoomNumber("https://www.huya.com/660000"));
    }

    [Fact]
    public void BuildRoomPageUrl_EncodesHost()
    {
        Assert.Equal("https://www.huya.com/lpl", HuyaUrlParser.BuildRoomPageUrl("lpl"));
    }
}
