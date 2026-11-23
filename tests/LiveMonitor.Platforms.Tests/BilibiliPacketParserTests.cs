using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// BilibiliPacketParser 的协议层测试：包头解析、zlib/brotli 嵌套、畸形包容错、
/// DANMU_MSG/礼物/SC 映射、op=8/op=3 回执识别与时间戳注入。
/// </summary>
public sealed class BilibiliPacketParserTests
{
    private const string RoomId = "1234";

    [Fact]
    public void ParseFrame_PlainDanmaku_ReturnsMappedMessage()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("你好，世界", "42", "alice"),
            BilibiliPacketParser.ProtoJson);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        var message = Assert.Single(result.Messages);
        Assert.Equal(PlatformType.Bilibili, message.Platform);
        Assert.Equal(RoomId, message.RoomId);
        Assert.Equal("42", message.SenderUid);
        Assert.Equal("alice", message.SenderName);
        Assert.Equal("你好，世界", message.Content);
        Assert.False(message.IsGiftOrSuperChat);
        Assert.False(result.AuthReplyReceived);
        Assert.False(result.HeartbeatReplyReceived);
    }

    [Fact]
    public void ParseFrame_MultipleStickyPackets_ParsesAll()
    {
        var first = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("第一条", "1", "a"),
            BilibiliPacketParser.ProtoJson);
        var second = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("第二条", "2", "b"),
            BilibiliPacketParser.ProtoJson);

        var frame = Concat(first, second);
        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("第一条", result.Messages[0].Content);
        Assert.Equal("第二条", result.Messages[1].Content);
    }

    [Fact]
    public void ParseFrame_ZlibNested_ParsesInnerPackets()
    {
        var inner = Concat(
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                DanmakuJson("压缩一", "1", "a"),
                BilibiliPacketParser.ProtoJson),
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                DanmakuJson("压缩二", "2", "b"),
                BilibiliPacketParser.ProtoJson));

        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            CompressZlib(inner),
            BilibiliPacketParser.ProtoZlib);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("压缩一", result.Messages[0].Content);
        Assert.Equal("压缩二", result.Messages[1].Content);
    }

    [Fact]
    public void ParseFrame_BrotliNested_ParsesInnerPackets()
    {
        var inner = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("brotli 弹幕", "9", "zoe"),
            BilibiliPacketParser.ProtoJson);

        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            CompressBrotli(inner),
            BilibiliPacketParser.ProtoBrotli);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        var message = Assert.Single(result.Messages);
        Assert.Equal("brotli 弹幕", message.Content);
        Assert.Equal("9", message.SenderUid);
    }

    [Fact]
    public void ParseFrame_HeaderLengthGreaterThan16_UsesHeaderLengthAsBodyOffset()
    {
        var body = Encoding.UTF8.GetBytes(DanmakuJson("padding header", "3", "c"));
        var frame = BuildRawPacket(
            BilibiliPacketParser.OpNotification,
            BilibiliPacketParser.ProtoJson,
            body,
            headerLength: 20);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        var message = Assert.Single(result.Messages);
        Assert.Equal("padding header", message.Content);
    }

    [Fact]
    public void ParseFrame_MalformedFrames_DoNotThrowAndYieldNothing()
    {
        var valid = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("好包", "1", "a"),
            BilibiliPacketParser.ProtoJson);

        // 长度字段大于实际剩余字节（截断）。
        var truncated = valid[..^3];
        // 包头声明长度小于包头。
        var badLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32BigEndian(badLength.AsSpan(0, 4), 8);
        // headerLen 小于 16。
        var badHeaderLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt16BigEndian(badHeaderLength.AsSpan(4, 2), 8);
        // 随机垃圾字节。
        var garbage = new byte[] { 0xFF, 0x00, 0x13, 0x37, 0x42, 0x00, 0x00, 0x01 };

        // zlib 声明但负载不是 zlib；brotli 同理。
        var badZlib = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            Encoding.UTF8.GetBytes("not zlib at all"),
            BilibiliPacketParser.ProtoZlib);
        var badBrotli = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            Encoding.UTF8.GetBytes("not brotli at all"),
            BilibiliPacketParser.ProtoBrotli);
        // op=5 但负载不是合法 JSON。
        var badJson = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            "{not-json",
            BilibiliPacketParser.ProtoJson);
        // 未知 op。
        var unknownOp = BilibiliPacketParser.BuildPacket(
            operation: 99,
            bodyText: DanmakuJson("未知操作码", "1", "a"),
            protoVersion: BilibiliPacketParser.ProtoJson);

        var frames = new[]
        {
            Array.Empty<byte>(),
            garbage,
            truncated,
            badLength,
            badHeaderLength,
            badZlib,
            badBrotli,
            badJson,
            unknownOp
        };

        foreach (var frame in frames)
        {
            var result = BilibiliPacketParser.ParseFrame(frame, RoomId);
            Assert.Empty(result.Messages);
        }
    }

    [Fact]
    public void ParseFrame_DanmakuWithGuestUid_IsAnonymousSender()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("游客弹幕", "0", "匿名"),
            BilibiliPacketParser.ProtoJson);

        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.Equal("0", message.SenderUid);
        Assert.True(message.IsAnonymousSender);
    }

    [Fact]
    public void ParseFrame_SendGift_MarksGiftOrSuperChat()
    {
        var json = """
            {"cmd":"SEND_GIFT","data":{"giftName":"辣条","num":3,"uid":7,"uname":"bob"}}
            """;
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            json,
            BilibiliPacketParser.ProtoJson);

        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.True(message.IsGiftOrSuperChat);
        Assert.Equal("7", message.SenderUid);
        Assert.Equal("bob", message.SenderName);
        Assert.Contains("辣条", message.Content);
        Assert.Contains("x3", message.Content);
    }

    [Fact]
    public void ParseFrame_SuperChat_MarksGiftOrSuperChatUsingUserInfoName()
    {
        var json = """
            {"cmd":"SUPER_CHAT_MESSAGE","data":{"uid":8,"message":"SC 内容","user_info":{"uname":"carol"}}}
            """;
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            json,
            BilibiliPacketParser.ProtoJson);

        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.True(message.IsGiftOrSuperChat);
        Assert.Equal("8", message.SenderUid);
        Assert.Equal("carol", message.SenderName);
        Assert.Equal("SC 内容", message.Content);
    }

    [Fact]
    public void ParseFrame_SuperChatVariants_AreIgnored()
    {
        var jpn = """
            {"cmd":"SUPER_CHAT_MESSAGE_JPN","data":{"uid":8,"message":"SC 内容","user_info":{"uname":"carol"}}}
            """;
        var delete = """
            {"cmd":"SUPER_CHAT_MESSAGE_DELETE","data":{"uid":8}}
            """;
        var frame = Concat(
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification, jpn, BilibiliPacketParser.ProtoJson),
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification, delete, BilibiliPacketParser.ProtoJson));

        Assert.Empty(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);
    }

    [Fact]
    public void ParseFrame_Timestamp_ComesFromInjectedClock()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpNotification,
            DanmakuJson("时钟", "1", "a"),
            BilibiliPacketParser.ProtoJson);

        var message = Assert.Single(
            BilibiliPacketParser.ParseFrame(frame, RoomId, () => fixedTime).Messages);

        Assert.Equal(fixedTime, message.Timestamp);
    }

    [Fact]
    public void ParseFrame_AuthReplyCode0_IsRecognized()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpAuthReply,
            """{"code":0}""",
            BilibiliPacketParser.ProtoHeartbeat);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.True(result.AuthReplyReceived);
        Assert.Equal(0, result.AuthReplyCode);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ParseFrame_AuthReplyFailureCode_ExposesCodeAndMessage()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpAuthReply,
            """{"code":-101,"message":"未登录"}""",
            BilibiliPacketParser.ProtoHeartbeat);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.True(result.AuthReplyReceived);
        Assert.Equal(-101, result.AuthReplyCode);
        Assert.Equal("未登录", result.AuthReplyMessage);
    }

    [Fact]
    public void ParseFrame_AuthReplyUnparsable_MarksFailureInsteadOfSuccess()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpAuthReply,
            "not-json",
            BilibiliPacketParser.ProtoHeartbeat);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.True(result.AuthReplyReceived);
        Assert.NotEqual(0, result.AuthReplyCode);
    }

    [Fact]
    public void ParseFrame_HeartbeatReply_IsRecognized()
    {
        var frame = BilibiliPacketParser.BuildPacket(
            BilibiliPacketParser.OpHeartbeatReply,
            """{"code":0}""",
            BilibiliPacketParser.ProtoHeartbeat);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.True(result.HeartbeatReplyReceived);
        Assert.False(result.AuthReplyReceived);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ParseFrame_AuthReplyAndDanmakuInSameFrame_BothRecognized()
    {
        var frame = Concat(
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpAuthReply,
                """{"code":0}""",
                BilibiliPacketParser.ProtoHeartbeat),
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                DanmakuJson("夹带弹幕", "5", "e"),
                BilibiliPacketParser.ProtoJson));

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.True(result.AuthReplyReceived);
        var message = Assert.Single(result.Messages);
        Assert.Equal("夹带弹幕", message.Content);
    }

    [Fact]
    public void ParseFrame_FourCompressionLayers_StillParsed()
    {
        var frame = WrapWithCompression(
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                DanmakuJson("四层", "1", "a"),
                BilibiliPacketParser.ProtoJson),
            layers: 4);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        var message = Assert.Single(result.Messages);
        Assert.Equal("四层", message.Content);
    }

    [Fact]
    public void ParseFrame_ExceedingMaxDecompressDepth_StopsParsing()
    {
        var frame = WrapWithCompression(
            BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                DanmakuJson("五层", "1", "a"),
                BilibiliPacketParser.ProtoJson),
            layers: 5);

        var result = BilibiliPacketParser.ParseFrame(frame, RoomId);

        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ParseFrame_StickerDanmaku_ParsesStickerFlagAndEmoteUrl()
    {
        var info = new object[]
        {
            new object?[]
            {
                0, 1, 25, 16777215, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                new { url = "https://example.invalid/sticker.png", emoticon_unique = "room_1" },
                0, 0
            },
            "[赞]",
            new object?[] { "42", "alice" }
        };

        var frame = BuildDanmakuFrame(info);
        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.True(message.IsSticker);
        Assert.Equal("[赞]", message.Content);
        var emote = Assert.Single(message.Emotes!);
        Assert.Equal("赞", emote.Name);
        Assert.Equal("https://example.invalid/sticker.png", emote.Url);
        Assert.Equal("room_1", emote.Id);
    }

    [Fact]
    public void ParseFrame_InlineEmote_ParsesEmoteMetadataFromExtraJson()
    {
        var extra = JsonSerializer.Serialize(new
        {
            emots = new Dictionary<string, object>
            {
                ["[doge]"] = new { url = "https://example.invalid/doge.png", emoticon_unique = "doge_1" },
                ["[unused]"] = new { url = "https://example.invalid/unused.png", emoticon_unique = "unused_1" }
            }
        });

        var info = new object[]
        {
            new object?[]
            {
                0, 1, 25, 16777215, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0,
                new { extra }
            },
            "主播[doge][doge] 好操作",
            new object?[] { "42", "alice" }
        };

        var frame = BuildDanmakuFrame(info);
        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.False(message.IsSticker);
        var emote = Assert.Single(message.Emotes!);
        Assert.Equal("doge", emote.Name);
        Assert.Equal("https://example.invalid/doge.png", emote.Url);
        Assert.Equal("doge_1", emote.Id);

        // 文本里没出现的房间表情不应塞进这条弹幕。
        Assert.DoesNotContain(message.Emotes!, candidate => candidate.Name == "unused");
    }

    [Fact]
    public void ParseFrame_StickerWithoutUrl_KeepsStickerFlagAndNullUrl()
    {
        var info = new object[]
        {
            new object?[]
            {
                0, 1, 25, 16777215, 0, 0, 0, 0, 0, 0, 0, 0, 1, null, 0, 0
            },
            "赞",
            new object?[] { "42", "alice" }
        };

        var frame = BuildDanmakuFrame(info);
        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.True(message.IsSticker);
        Assert.Null(Assert.Single(message.Emotes!).Url);
    }

    [Fact]
    public void ParseFrame_MalformedEmoteExtra_FallsBackToPlainMessage()
    {
        var info = new object[]
        {
            new object?[]
            {
                0, 1, 25, 16777215, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0,
                new { extra = "{not-json" }
            },
            "普通弹幕[doge]",
            new object?[] { "42", "alice" }
        };

        var frame = BuildDanmakuFrame(info);
        var message = Assert.Single(BilibiliPacketParser.ParseFrame(frame, RoomId).Messages);

        Assert.False(message.IsSticker);
        Assert.Null(message.Emotes);
        Assert.Equal("普通弹幕[doge]", message.Content);
    }

    [Fact]
    public void BuildAuthPacket_HasAuthHeaderAndProtocolJson()
    {
        var packet = BilibiliPacketParser.BuildAuthPacket(1234, 42, "token-abc", "buvid-xyz");

        Assert.Equal(BilibiliPacketParser.OpClientAuth, ReadOperation(packet));
        Assert.Equal(BilibiliPacketParser.ProtoHeartbeat, ReadProtoVersion(packet));
        Assert.Equal(packet.Length, ReadPacketLength(packet));

        var json = Encoding.UTF8.GetString(packet, BilibiliPacketParser.HeaderSize, packet.Length - BilibiliPacketParser.HeaderSize);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(42, root.GetProperty("uid").GetInt64());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("roomid").ValueKind);
        Assert.Equal(1234, root.GetProperty("roomid").GetInt64());
        Assert.Equal(3, root.GetProperty("protover").GetInt32());
        Assert.Equal("buvid-xyz", root.GetProperty("buvid").GetString());
        Assert.Equal("web", root.GetProperty("platform").GetString());
        Assert.Equal(2, root.GetProperty("type").GetInt32());
        Assert.Equal("token-abc", root.GetProperty("key").GetString());
    }

    [Fact]
    public void BuildHeartbeatPacket_IsOp2WithObjectObjectBody()
    {
        var packet = BilibiliPacketParser.BuildHeartbeatPacket();

        Assert.Equal(BilibiliPacketParser.OpClientHeartbeat, ReadOperation(packet));
        Assert.Equal(BilibiliPacketParser.ProtoHeartbeat, ReadProtoVersion(packet));
        Assert.Equal(
            "[object Object]",
            Encoding.UTF8.GetString(packet, BilibiliPacketParser.HeaderSize, packet.Length - BilibiliPacketParser.HeaderSize));
    }

    private static int ReadPacketLength(byte[] packet)
        => BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(0, 4));

    private static short ReadProtoVersion(byte[] packet)
        => BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(6, 2));

    private static int ReadOperation(byte[] packet)
        => BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4));

    private static string DanmakuJson(string content, string uid, string uname)
        => $"{{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16777215],{JsonSerializer.Serialize(content)},[{uid},{JsonSerializer.Serialize(uname)},0,0]]}}";

    private static byte[] BuildDanmakuFrame(object[] info)
    {
        var json = JsonSerializer.Serialize(new { cmd = "DANMU_MSG", info });
        return BilibiliPacketParser.BuildPacket(BilibiliPacketParser.OpNotification, json, BilibiliPacketParser.ProtoJson);
    }

    private static byte[] BuildRawPacket(int operation, short protoVersion, byte[] body, short headerLength)
    {
        var packet = new byte[headerLength + body.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[..4], packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(4, 2), headerLength);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(6, 2), protoVersion);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(12, 4), 1);
        body.CopyTo(span[headerLength..]);
        return packet;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] WrapWithCompression(byte[] inner, int layers)
    {
        var current = inner;
        for (var i = 0; i < layers; i++)
        {
            current = BilibiliPacketParser.BuildPacket(
                BilibiliPacketParser.OpNotification,
                CompressZlib(current),
                BilibiliPacketParser.ProtoZlib);
        }

        return current;
    }

    private static byte[] CompressZlib(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] CompressBrotli(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(payload);
        }

        return output.ToArray();
    }
}
