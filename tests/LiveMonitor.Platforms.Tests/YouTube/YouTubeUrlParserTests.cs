using LiveMonitor.Plugins.YouTube;
using Xunit;

namespace LiveMonitor.Platforms.Tests.YouTube;

public sealed class YouTubeUrlParserTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    [Theory]
    [InlineData($"https://www.youtube.com/watch?v={VideoId}")]
    [InlineData($"https://youtube.com/watch?v={VideoId}&t=10s")]
    [InlineData($"https://m.youtube.com/watch?v={VideoId}")]
    [InlineData($"https://www.youtube.com/live/{VideoId}?si=abc")]
    [InlineData($"https://youtu.be/{VideoId}?t=5")]
    [InlineData("https://www.youtube.com/@LofiGirl/live")]
    [InlineData("https://www.youtube.com/@LofiGirl")]
    [InlineData("https://www.youtube.com/channel/UCabc12345678901234567890/live")]
    [InlineData(VideoId)]
    [InlineData("@LofiGirl")]
    public void CanHandle_RecognizedForms_ReturnsTrue(string value)
    {
        Assert.True(YouTubeUrlParser.CanHandle(value));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/somechannel")]
    [InlineData("https://www.bilibili.com/12345")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://youtu.be/")]
    public void CanHandle_UnrecognizedForms_ReturnsFalse(string? value)
    {
        Assert.False(YouTubeUrlParser.CanHandle(value));
    }

    [Theory]
    [InlineData($"https://www.youtube.com/watch?v={VideoId}", VideoId)]
    [InlineData($"https://youtube.com/watch?v={VideoId}&t=10s", VideoId)]
    [InlineData($"https://www.youtube.com/live/{VideoId}", VideoId)]
    [InlineData($"https://youtu.be/{VideoId}", VideoId)]
    [InlineData("https://www.youtube.com/@LofiGirl/live", "@LofiGirl")]
    [InlineData("https://www.youtube.com/@LofiGirl/streams", "@LofiGirl")]
    [InlineData("https://www.youtube.com/channel/UCabc12345678901234567890/live", "channel:UCabc12345678901234567890")]
    [InlineData("  dQw4w9WgXcQ  ", VideoId)]
    public void ExtractRoomId_RecognizedForms_ReturnsNormalizedId(string value, string expected)
    {
        Assert.Equal(expected, YouTubeUrlParser.ExtractRoomId(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://www.twitch.tv/somechannel")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("not a url")]
    public void ExtractRoomId_UnrecognizedForms_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => YouTubeUrlParser.ExtractRoomId(value));
    }

    [Fact]
    public void BuildLiveUrl_VideoId_UsesWatchPage()
    {
        Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", YouTubeUrlParser.BuildLiveUrl(VideoId));
    }

    [Fact]
    public void BuildLiveUrl_Handle_UsesLivePage()
    {
        Assert.Equal("https://www.youtube.com/@LofiGirl/live", YouTubeUrlParser.BuildLiveUrl("@LofiGirl"));
    }

    [Fact]
    public void BuildLiveUrl_ChannelId_UsesChannelLivePage()
    {
        Assert.Equal(
            "https://www.youtube.com/channel/UCabc12345678901234567890/live",
            YouTubeUrlParser.BuildLiveUrl("channel:UCabc12345678901234567890"));
    }

    [Fact]
    public void IsVideoId_EnforcesElevenCharacters()
    {
        Assert.True(YouTubeUrlParser.IsVideoId(VideoId));
        Assert.False(YouTubeUrlParser.IsVideoId("short"));
        Assert.False(YouTubeUrlParser.IsVideoId("@LofiGirl"));
    }
}
