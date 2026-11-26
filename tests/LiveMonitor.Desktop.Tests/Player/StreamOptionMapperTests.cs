using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

/// <summary>
/// <see cref="StreamOptionMapper"/> 的单元测试：demuxer 缓存档位、默认 UA、请求头大小写
/// 查找、CR/LF 清理、Cookie 拼装与分离音频地址。
/// </summary>
public sealed class StreamOptionMapperTests
{
    private static StreamQuality Quality(
        int height = 1080,
        Dictionary<string, string>? headers = null,
        string? audioStreamUrl = null)
        => new(
            "hd",
            "测试档位",
            height,
            60,
            "https://example.com/video.m3u8",
            headers ?? new Dictionary<string, string>(),
            audioStreamUrl);

    [Theory]
    [InlineData(720, "8M")]
    [InlineData(1080, "8M")]
    [InlineData(2160, "8M")]
    [InlineData(480, "3M")]
    [InlineData(0, "3M")]
    public void Build_SelectsDemuxerCacheByHeight(int height, string expected)
    {
        Assert.Equal(expected, StreamOptionMapper.Build(Quality(height)).DemuxerMaxBytes);
    }

    [Fact]
    public void Build_WithoutUserAgentHeader_UsesDefaultBrowserUserAgent()
    {
        var options = StreamOptionMapper.Build(Quality());

        Assert.Equal(StreamOptionMapper.DefaultUserAgent, options.UserAgent);
        Assert.Contains("Chrome/131", options.UserAgent);
    }

    [Fact]
    public void Build_UserAgentHeader_IsCaseInsensitiveAndOverridesDefault()
    {
        var headers = new Dictionary<string, string> { ["user-agent"] = "CustomAgent/1.0" };
        var options = StreamOptionMapper.Build(Quality(headers: headers));

        Assert.Equal("CustomAgent/1.0", options.UserAgent);
    }

    [Fact]
    public void Build_SanitizesCrLfInHeaderValues()
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "Agent\r\nInjected: 1",
            ["Referer"] = "https://example.com/page\r\n",
        };

        var options = StreamOptionMapper.Build(Quality(headers: headers));

        Assert.Equal("Agent Injected: 1", options.UserAgent);
        Assert.Equal("https://example.com/page", options.Referrer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_BlankReferer_ReturnsNull(string? referer)
    {
        var headers = referer is null ? null : new Dictionary<string, string> { ["Referer"] = referer };
        var options = StreamOptionMapper.Build(Quality(headers: headers));

        Assert.Null(options.Referrer);
    }

    [Fact]
    public void Build_Cookie_ComposesMpvHttpHeaderFields()
    {
        var headers = new Dictionary<string, string> { ["Cookie"] = "a=b; c=d" };

        var options = StreamOptionMapper.Build(Quality(headers: headers));

        Assert.Equal("Cookie: a=b; c=d", options.HttpHeaderFields);
    }

    [Fact]
    public void Build_BlankCookie_ReturnsNullHeaderFields()
    {
        var headers = new Dictionary<string, string> { ["Cookie"] = "  " };

        Assert.Null(StreamOptionMapper.Build(Quality(headers: headers)).HttpHeaderFields);
    }

    [Fact]
    public void Build_NoSeparateAudio_AudioFileIsNull()
    {
        Assert.Null(StreamOptionMapper.Build(Quality()).AudioFile);
    }

    [Fact]
    public void Build_SeparateAudio_TrimsAudioStreamUrl()
    {
        var options = StreamOptionMapper.Build(Quality(audioStreamUrl: "  https://example.com/audio.m3u8  "));

        Assert.Equal("https://example.com/audio.m3u8", options.AudioFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_BlankAudioStreamUrl_HasSeparateAudioFalse_AudioFileIsNull(string? audioStreamUrl)
    {
        Assert.Null(StreamOptionMapper.Build(Quality(audioStreamUrl: audioStreamUrl)).AudioFile);
    }

    [Fact]
    public void FindHeader_MatchesCaseInsensitively()
    {
        var headers = new Dictionary<string, string> { ["REFERER"] = "https://x.test/a" };

        Assert.Equal("https://x.test/a", StreamOptionMapper.FindHeader(Quality(headers: headers), "Referer"));
    }

    [Fact]
    public void FindHeader_Missing_ReturnsNull()
    {
        Assert.Null(StreamOptionMapper.FindHeader(Quality(), "Cookie"));
    }

    [Fact]
    public void SanitizeHeaderValue_RemovesCrReplacesLfAndTrims()
    {
        Assert.Equal("a b", StreamOptionMapper.SanitizeHeaderValue("  a\r\nb\r\n  "));
    }
}
