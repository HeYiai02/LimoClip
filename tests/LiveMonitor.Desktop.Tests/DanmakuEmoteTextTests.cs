using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class DanmakuEmoteTextTests
{
    [Fact]
    public void Split_PlainText_ReturnsSingleTextPart()
    {
        var parts = DanmakuEmoteText.Split("主播厉害");

        var part = Assert.Single(parts);
        Assert.Equal("主播厉害", part.Text);
        Assert.Null(part.EmoteName);
    }

    [Fact]
    public void Split_InlineEmote_SplitsTextAndEmote()
    {
        var parts = DanmakuEmoteText.Split("主播厉害[doge]下次还来");

        Assert.Equal(3, parts.Count);
        Assert.Equal("主播厉害", parts[0].Text);
        Assert.Null(parts[0].EmoteName);
        Assert.Null(parts[1].Text);
        Assert.Equal("doge", parts[1].EmoteName);
        Assert.Equal("下次还来", parts[2].Text);
        Assert.Null(parts[2].EmoteName);
    }

    [Fact]
    public void Split_ConsecutiveEmotes_ProducesNoEmptyTextParts()
    {
        var parts = DanmakuEmoteText.Split("[doge][打call]");

        Assert.Equal(2, parts.Count);
        Assert.Equal("doge", parts[0].EmoteName);
        Assert.Equal("打call", parts[1].EmoteName);
    }

    [Fact]
    public void Split_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(DanmakuEmoteText.Split(null));
        Assert.Empty(DanmakuEmoteText.Split(string.Empty));
    }

    [Fact]
    public void Split_UnclosedBracket_IsTreatedAsText()
    {
        var parts = DanmakuEmoteText.Split("abc[doge");

        var part = Assert.Single(parts);
        Assert.Equal("abc[doge", part.Text);
    }

    [Fact]
    public void ResolveImageUrl_MatchesNameIgnoringCase()
    {
        var emotes = new[] { new DanmakuEmote("doge", "https://example.com/doge.png") };

        Assert.Equal("https://example.com/doge.png", DanmakuEmoteText.ResolveImageUrl(emotes, "DOGE"));
    }

    [Fact]
    public void ResolveImageUrl_NameMismatchOrNullUrl_ReturnsNull()
    {
        var emotes = new[]
        {
            new DanmakuEmote("doge", "https://example.com/doge.png"),
            new DanmakuEmote("打call"),
        };

        Assert.Null(DanmakuEmoteText.ResolveImageUrl(emotes, "unknown"));
        Assert.Null(DanmakuEmoteText.ResolveImageUrl(emotes, "打call"));
    }

    [Fact]
    public void ResolveImageUrl_NullEmotes_ReturnsNull()
    {
        Assert.Null(DanmakuEmoteText.ResolveImageUrl(null, "doge"));
        Assert.Null(DanmakuEmoteText.ResolveImageUrl(Array.Empty<DanmakuEmote>(), "doge"));
    }
}
