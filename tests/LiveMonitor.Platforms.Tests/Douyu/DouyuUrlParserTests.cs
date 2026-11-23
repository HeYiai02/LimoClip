using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuUrlParserTests
{
    [Theory]
    [InlineData("https://www.douyu.com/9999")]
    [InlineData("https://www.douyu.com/9999?dyshid=abc")]
    [InlineData("https://www.douyu.com/9999/")]
    [InlineData("http://douyu.com/9999")]
    [InlineData("https://m.douyu.com/9999")]
    [InlineData("www.douyu.com/9999")]
    public void CanHandle_AcceptsDouyuLinks(string url)
    {
        Assert.True(DouyuUrlParser.CanHandle(url));
    }

    [Theory]
    [InlineData("9999")]
    [InlineData("https://live.bilibili.com/9999")]
    [InlineData("https://www.twitch.tv/eslcs")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void CanHandle_RejectsNonDouyuInputs(string? url)
    {
        Assert.False(DouyuUrlParser.CanHandle(url));
    }

    [Theory]
    [InlineData("https://www.douyu.com/9999", "9999")]
    [InlineData("https://www.douyu.com/9999?dyshid=abc", "9999")]
    [InlineData("https://m.douyu.com/123456", "123456")]
    [InlineData("www.douyu.com/88888", "88888")]
    public void ExtractRoomId_ParsesNumericRoom(string url, string expected)
    {
        Assert.Equal(expected, DouyuUrlParser.ExtractRoomId(url));
    }

    [Fact]
    public void ExtractRoomId_AcceptsNumericInput()
    {
        Assert.Equal("9999", DouyuUrlParser.ExtractRoomId("9999"));
    }

    [Fact]
    public void ExtractRoomId_RejectsNonDouyuUrl()
    {
        var error = Assert.Throws<ArgumentException>(() => DouyuUrlParser.ExtractRoomId("https://live.bilibili.com/9999"));
        Assert.Contains("不支持的斗鱼直播间链接", error.Message);
    }

    [Fact]
    public void ExtractRoomId_RejectsLinkWithoutRoomNumber()
    {
        var error = Assert.Throws<ArgumentException>(() => DouyuUrlParser.ExtractRoomId("https://www.douyu.com/topic/something"));
        Assert.Contains("无法从斗鱼链接中解析出房间号", error.Message);
    }

    [Fact]
    public void TryExtractRoomId_ReturnsFalseForUnsupported()
    {
        Assert.False(DouyuUrlParser.TryExtractRoomId("https://www.twitch.tv/eslcs", out var roomId));
        Assert.Null(roomId);
    }

    [Fact]
    public void TryExtractRoomId_ReturnsRoomForDouyuLink()
    {
        Assert.True(DouyuUrlParser.TryExtractRoomId("https://www.douyu.com/7777", out var roomId));
        Assert.Equal("7777", roomId);
    }

    [Theory]
    [InlineData("9999", true)]
    [InlineData("0", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void IsRoomNumber_ChecksDigitsOnly(string value, bool expected)
    {
        Assert.Equal(expected, DouyuUrlParser.IsRoomNumber(value));
    }
}
