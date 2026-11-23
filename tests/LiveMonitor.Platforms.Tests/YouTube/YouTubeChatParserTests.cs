using LiveMonitor.Core.Enums;
using LiveMonitor.Plugins.YouTube;
using Xunit;

namespace LiveMonitor.Platforms.Tests.YouTube;

public sealed class YouTubeChatParserTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    private const string PageHtml = """
        <html><head><script>
        var ytcfg = {"INNERTUBE_API_KEY":"TEST_API_KEY","INNERTUBE_CLIENT_VERSION":"2.20250101.00.00"};
        var ytInitialData = {"contents":{"liveChatRenderer":{"continuation":"BOOTSTRAP_CONTINUATION_TOKEN_1234567890","isLive":true}}};
        </script></head><body></body></html>
        """;

    private const string ChatJson = """
        {
          "continuationContents": {
            "liveChatContinuation": {
              "actions": [
                { "addChatItemAction": { "item": { "liveChatTextMessageRenderer": {
                    "authorName": { "simpleText": "Alice" },
                    "authorExternalChannelId": "UCAlice",
                    "message": { "runs": [ { "text": "hello " }, { "text": "world" } ] } } } } },
                { "addChatItemAction": { "item": { "liveChatTextMessageRenderer": {
                    "authorName": { "runs": [ { "text": "Bob" } ] },
                    "authorExternalChannelId": "UCBob",
                    "message": { "runs": [ { "text": "second message" } ] } } } } },
                { "addChatItemAction": { "item": { "liveChatPaidMessageRenderer": {
                    "authorName": { "simpleText": "Carol" },
                    "authorExternalChannelId": "UCCarol",
                    "message": { "runs": [ { "text": "super chat text" } ] },
                    "purchaseAmountText": { "simpleText": "$5.00" } } } } },
                { "addChatItemAction": { "item": { "liveChatPaidStickerRenderer": {
                    "authorName": { "simpleText": "Dave" },
                    "authorExternalChannelId": "UCDave" } } } },
                { "addChatItemAction": { "item": { "liveChatMembershipItemRenderer": {
                    "authorName": { "simpleText": "Eve" },
                    "authorExternalChannelId": "UCEve",
                    "message": { "runs": [ { "text": "joined" } ] } } } } }
              ],
              "continuations": [
                { "invalidationContinuationData": { "continuation": "NEXT_CONTINUATION_TOKEN", "timeoutMs": 4000 } }
              ]
            }
          }
        }
        """;

    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 23, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TryParseBootstrap_ValidPage_ReturnsKeys()
    {
        var bootstrap = YouTubeChatParser.TryParseBootstrap(PageHtml);

        Assert.NotNull(bootstrap);
        Assert.Equal("TEST_API_KEY", bootstrap!.ApiKey);
        Assert.Equal("2.20250101.00.00", bootstrap.ClientVersion);
        Assert.Equal("BOOTSTRAP_CONTINUATION_TOKEN_1234567890", bootstrap.Continuation);
    }

    [Fact]
    public void TryParseBootstrap_MissingApiKey_ReturnsNull()
    {
        Assert.Null(YouTubeChatParser.TryParseBootstrap("<html>nothing here</html>"));
    }

    [Fact]
    public void TryParseBootstrap_MissingContinuation_ReturnsNull()
    {
        const string html = """<script>var ytcfg = {"INNERTUBE_API_KEY":"KEY"};</script>""";

        Assert.Null(YouTubeChatParser.TryParseBootstrap(html));
    }

    [Fact]
    public void ParseChatResponse_ParsesTextSuperChatAndSticker()
    {
        var page = YouTubeChatParser.ParseChatResponse(ChatJson, VideoId, PlatformType.YouTube, ReceivedAt);

        Assert.Equal(4, page.Messages.Count);

        var first = page.Messages[0];
        Assert.Equal("hello world", first.Content);
        Assert.Equal("Alice", first.SenderName);
        Assert.Equal("UCAlice", first.SenderUid);
        Assert.False(first.IsGiftOrSuperChat);
        Assert.Equal(VideoId, first.RoomId);
        Assert.Equal(ReceivedAt, first.Timestamp);

        var second = page.Messages[1];
        Assert.Equal("second message", second.Content);
        Assert.Equal("Bob", second.SenderName);

        var superChat = page.Messages[2];
        Assert.Equal("super chat text", superChat.Content);
        Assert.True(superChat.IsGiftOrSuperChat);
        Assert.False(superChat.IsSticker);

        var paidSticker = page.Messages[3];
        Assert.Equal("超级贴纸", paidSticker.Content);
        Assert.Equal("Dave", paidSticker.SenderName);
        Assert.True(paidSticker.IsGiftOrSuperChat);
        Assert.True(paidSticker.IsSticker);

        Assert.Equal("NEXT_CONTINUATION_TOKEN", page.NextContinuation);
        Assert.Equal(TimeSpan.FromSeconds(4), page.NextInterval);
    }

    [Fact]
    public void ParseChatResponse_NoContinuations_ReturnsNullNextToken()
    {
        const string json = """
            { "continuationContents": { "liveChatContinuation": {
                "actions": [],
                "continuations": []
            } } }
            """;

        var page = YouTubeChatParser.ParseChatResponse(json, VideoId, PlatformType.YouTube, ReceivedAt);

        Assert.Empty(page.Messages);
        Assert.Null(page.NextContinuation);
        Assert.Equal(YouTubeChatParser.DefaultPollInterval, page.NextInterval);
    }

    [Fact]
    public void ParseChatResponse_MissingLiveChatContinuation_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => YouTubeChatParser.ParseChatResponse("{}", VideoId, PlatformType.YouTube, ReceivedAt));
    }

    [Fact]
    public void ParseChatResponse_BrokenJson_Throws()
    {
        Assert.ThrowsAny<Exception>(
            () => YouTubeChatParser.ParseChatResponse("{broken", VideoId, PlatformType.YouTube, ReceivedAt));
    }

    [Theory]
    [InlineData(500, 1000)]
    [InlineData(4000, 4000)]
    [InlineData(60000, 15000)]
    [InlineData(0, 3000)]
    public void NormalizeInterval_ClampsToAllowedRange(double timeoutMs, double expectedMs)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            YouTubeChatParser.NormalizeInterval(timeoutMs));
    }

    [Fact]
    public void BuildChatRequestBody_ContainsContextAndContinuation()
    {
        var body = YouTubeChatParser.BuildChatRequestBody("TOKEN", "2.20250101.00.00");

        Assert.Contains("\"continuation\":\"TOKEN\"", body);
        Assert.Contains("\"clientName\":\"WEB\"", body);
        Assert.Contains("\"clientVersion\":\"2.20250101.00.00\"", body);
        Assert.Contains("\"isReplay\":false", body);
    }
}
