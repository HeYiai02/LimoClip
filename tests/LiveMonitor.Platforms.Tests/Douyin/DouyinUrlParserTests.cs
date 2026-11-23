using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinUrlParserTests
{
    [Theory]
    [InlineData("https://live.douyin.com/141601416352", true)]
    [InlineData("live.douyin.com/141601416352", true)]
    [InlineData("http://live.douyin.com/141601416352", true)]
    [InlineData("https://www.douyin.com/root/live/141601416352", true)]
    [InlineData("https://live.douyin.com/141601416352?from=share", true)]
    [InlineData("141601416352", false)]
    [InlineData("https://live.bilibili.com/7734200", false)]
    [InlineData("https://www.twitch.tv/eslcs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_RecognizesOnlyDouyinLinks(string? url, bool expected)
    {
        Assert.Equal(expected, DouyinUrlParser.CanHandle(url));
    }

    [Theory]
    [InlineData("https://live.douyin.com/141601416352", "141601416352")]
    [InlineData("live.douyin.com/141601416352", "141601416352")]
    [InlineData("https://www.douyin.com/root/live/141601416352", "141601416352")]
    [InlineData("https://live.douyin.com/141601416352?from=share", "141601416352")]
    [InlineData("141601416352", "141601416352")]
    public void ExtractRoomId_ReturnsWebRid(string url, string expected)
    {
        Assert.Equal(expected, DouyinUrlParser.ExtractRoomId(url));
    }

    [Theory]
    [InlineData("https://live.douyin.com/")]
    [InlineData("https://live.douyin.com/abc")]
    [InlineData("https://example.com/141601416352")]
    [InlineData("")]
    public void ExtractRoomId_RejectsInvalidInput(string url)
    {
        Assert.Throws<ArgumentException>(() => DouyinUrlParser.ExtractRoomId(url));
    }

    [Fact]
    public void TryExtractRoomId_ReturnsFalseForInvalidInput()
    {
        Assert.False(DouyinUrlParser.TryExtractRoomId("https://example.com/141601416352", out _));
        Assert.True(DouyinUrlParser.TryExtractRoomId("https://live.douyin.com/141601416352", out var roomId));
        Assert.Equal("141601416352", roomId);
    }

    [Theory]
    [InlineData("141601416352", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("abcdef", false)]
    [InlineData("", false)]
    public void IsRoomNumber_ChecksDigitLength(string value, bool expected)
    {
        Assert.Equal(expected, DouyinUrlParser.IsRoomNumber(value));
    }

    [Fact]
    public void EnsureScheme_AddsHttps()
    {
        Assert.Equal("https://live.douyin.com/1", DouyinUrlParser.EnsureScheme("live.douyin.com/1"));
        Assert.Equal("http://live.douyin.com/1", DouyinUrlParser.EnsureScheme("http://live.douyin.com/1"));
    }

    [Fact]
    public void BuildLiveUrl_FormatsRoomPage()
    {
        Assert.Equal("https://live.douyin.com/141601416352", DouyinUrlParser.BuildLiveUrl("141601416352"));
    }
}
