using System.Text;
using LiveMonitor.Plugins.Douyin;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyin;

public sealed class DouyinProtoTests
{
    [Fact]
    public void BuildPushFrame_Heartbeat_ContainsOnlyPayloadType()
    {
        var frame = DouyinProto.BuildPushFrame("hb", Array.Empty<byte>(), 0);
        var parsed = DouyinProto.ParsePushFrame(frame);

        Assert.Equal("hb", parsed.PayloadType);
        Assert.Empty(parsed.Payload);
        Assert.Equal(0UL, parsed.LogId);
    }

    [Fact]
    public void BuildPushFrame_Ack_CarriesLogIdAndPayload()
    {
        var payload = Encoding.UTF8.GetBytes("cursor-123");
        var frame = DouyinProto.BuildPushFrame("ack", payload, 987654321);
        var parsed = DouyinProto.ParsePushFrame(frame);

        Assert.Equal("ack", parsed.PayloadType);
        Assert.Equal(987654321UL, parsed.LogId);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void ParsePushFrame_ReadsBinaryPayload()
    {
        var responseBytes = DouyinTestData.BuildResponse(
            new[] { ("WebcastChatMessage", DouyinTestData.BuildChatPayload(42, "测试用户", "你好抖音")) },
            internalExt: "ext-1",
            needAck: true);
        var frameBytes = DouyinProto.BuildPushFrame(string.Empty, DouyinTestData.Gzip(responseBytes), 123);

        var frame = DouyinProto.ParsePushFrame(frameBytes);
        var decompressed = DouyinProto.Decompress(frame.Payload);
        var response = DouyinProto.ParseResponse(decompressed);

        Assert.Equal(123UL, frame.LogId);
        Assert.True(response.NeedAck);
        Assert.Equal("ext-1", response.InternalExt);
        var message = Assert.Single(response.Messages);
        Assert.Equal("WebcastChatMessage", message.Method);
    }

    [Fact]
    public void ParseChatMessage_ReadsChatFields()
    {
        var payload = DouyinTestData.BuildChatPayload(123456789, "观众甲", "主播加油");
        var chat = DouyinProto.ParseChatMessage("WebcastChatMessage", payload);

        Assert.NotNull(chat);
        Assert.Equal(123456789UL, chat!.UserId);
        Assert.Equal("观众甲", chat.NickName);
        Assert.Equal("主播加油", chat.Content);
        Assert.False(chat.IsGift);
    }

    [Fact]
    public void ParseChatMessage_UnknownMethod_ReturnsNull()
    {
        Assert.Null(DouyinProto.ParseChatMessage("WebcastLikeMessage", new byte[] { 0x08, 0x01 }));
    }

    [Fact]
    public void ParseResponse_MultipleMessages()
    {
        var responseBytes = DouyinTestData.BuildResponse(
            new[]
            {
                ("WebcastChatMessage", DouyinTestData.BuildChatPayload(1, "甲", "一")),
                ("WebcastChatMessage", DouyinTestData.BuildChatPayload(2, "乙", "二")),
                ("WebcastMemberMessage", new byte[] { 0x08, 0x00 }),
            },
            internalExt: string.Empty,
            needAck: false);

        var response = DouyinProto.ParseResponse(responseBytes);

        Assert.False(response.NeedAck);
        Assert.Equal(3, response.Messages.Count);
    }

    [Fact]
    public void Decompress_InvalidGzip_Throws()
    {
        Assert.ThrowsAny<Exception>(() => DouyinProto.Decompress(new byte[] { 0x01, 0x02, 0x03 }));
    }
}
