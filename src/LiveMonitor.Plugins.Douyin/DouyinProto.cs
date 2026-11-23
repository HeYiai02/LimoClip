using System.IO.Compression;
using System.Text;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// 抖音弹幕 Protobuf 最小实现：只覆盖 PushFrame / Response / Message / ChatMessage / GiftMessage 等
/// 核心消息体（字段号来自公开的 webcast proto 定义），避免引入 Google.Protobuf 运行时与 .proto 编译步骤。
/// </summary>
internal static class DouyinProto
{
    internal sealed record PushFrameData(ulong SeqId, ulong LogId, string PayloadType, byte[] Payload);

    internal sealed record ResponseData(
        IReadOnlyList<ResponseMessage> Messages,
        string InternalExt,
        bool NeedAck);

    internal sealed record ResponseMessage(string Method, byte[] Payload);

    internal sealed record ChatMessageData(ulong UserId, string NickName, string Content, bool IsGift);

    public static PushFrameData ParsePushFrame(byte[] data)
    {
        ulong seqId = 0, logId = 0;
        var payloadType = string.Empty;
        var payload = Array.Empty<byte>();
        var i = 0;
        while (i < data.Length)
        {
            var (field, wire) = ReadTag(data, ref i);
            switch (wire)
            {
                case 0:
                {
                    var value = ReadVarint(data, ref i);
                    if (field == 1) seqId = value;
                    else if (field == 2) logId = value;
                    break;
                }
                case 2:
                {
                    var length = checked((int)ReadVarint(data, ref i));
                    if (field == 7) payloadType = Encoding.UTF8.GetString(data, i, length);
                    else if (field == 8) payload = data.AsSpan(i, length).ToArray();
                    i += length;
                    break;
                }
                default:
                    Skip(data, ref i, wire);
                    break;
            }
        }

        return new PushFrameData(seqId, logId, payloadType, payload);
    }

    public static ResponseData ParseResponse(byte[] data)
    {
        var messages = new List<ResponseMessage>();
        var internalExt = string.Empty;
        var needAck = false;
        var i = 0;
        while (i < data.Length)
        {
            var (field, wire) = ReadTag(data, ref i);
            switch (wire)
            {
                case 0:
                {
                    var value = ReadVarint(data, ref i);
                    if (field == 9) needAck = value != 0;
                    break;
                }
                case 2:
                {
                    var length = checked((int)ReadVarint(data, ref i));
                    if (field == 1) messages.Add(ParseMessage(data.AsSpan(i, length).ToArray()));
                    else if (field == 5) internalExt = Encoding.UTF8.GetString(data, i, length);
                    i += length;
                    break;
                }
                default:
                    Skip(data, ref i, wire);
                    break;
            }
        }

        return new ResponseData(messages, internalExt, needAck);
    }

    private static ResponseMessage ParseMessage(byte[] data)
    {
        var method = string.Empty;
        var payload = Array.Empty<byte>();
        var i = 0;
        while (i < data.Length)
        {
            var (field, wire) = ReadTag(data, ref i);
            switch (wire)
            {
                case 0:
                    ReadVarint(data, ref i);
                    break;
                case 2:
                {
                    var length = checked((int)ReadVarint(data, ref i));
                    if (field == 1) method = Encoding.UTF8.GetString(data, i, length);
                    else if (field == 2) payload = data.AsSpan(i, length).ToArray();
                    i += length;
                    break;
                }
                default:
                    Skip(data, ref i, wire);
                    break;
            }
        }

        return new ResponseMessage(method, payload);
    }

    /// <summary>解析聊天/礼物/表情消息；无法识别的消息返回 null。</summary>
    public static ChatMessageData? ParseChatMessage(string method, byte[] payload) => method switch
    {
        "WebcastChatMessage" => ParseWebcastChat(payload, isGift: false),
        "WebcastGiftMessage" => ParseGift(payload),
        "WebcastEmojiChatMessage" => ParseEmojiChat(payload),
        _ => null,
    };

    private static ChatMessageData? ParseWebcastChat(byte[] payload, bool isGift)
    {
        ulong userId = 0;
        var nick = string.Empty;
        var content = string.Empty;
        var i = 0;
        while (i < payload.Length)
        {
            var (field, wire) = ReadTag(payload, ref i);
            switch (wire)
            {
                case 0:
                    ReadVarint(payload, ref i);
                    break;
                case 2:
                {
                    var length = checked((int)ReadVarint(payload, ref i));
                    if (field == 3) content = Encoding.UTF8.GetString(payload, i, length);
                    else if (field == 2) (userId, nick) = ParseUser(payload.AsSpan(i, length).ToArray());
                    i += length;
                    break;
                }
                default:
                    Skip(payload, ref i, wire);
                    break;
            }
        }

        return new ChatMessageData(userId, string.IsNullOrWhiteSpace(nick) ? "未知用户" : nick, content, isGift);
    }

    private static ChatMessageData? ParseGift(byte[] payload)
    {
        ulong userId = 0;
        var nick = string.Empty;
        var giftName = string.Empty;
        var i = 0;
        while (i < payload.Length)
        {
            var (field, wire) = ReadTag(payload, ref i);
            switch (wire)
            {
                case 0:
                    ReadVarint(payload, ref i);
                    break;
                case 2:
                {
                    var length = checked((int)ReadVarint(payload, ref i));
                    if (field == 7) (userId, nick) = ParseUser(payload.AsSpan(i, length).ToArray());
                    else if (field == 15) giftName = ParseGiftName(payload.AsSpan(i, length).ToArray());
                    i += length;
                    break;
                }
                default:
                    Skip(payload, ref i, wire);
                    break;
            }
        }

        var content = string.IsNullOrWhiteSpace(giftName) ? "送出礼物 🎁" : $"送出礼物：{giftName}";
        return new ChatMessageData(userId, string.IsNullOrWhiteSpace(nick) ? "未知用户" : nick, content, true);
    }

    private static string ParseGiftName(byte[] giftStruct)
    {
        var i = 0;
        while (i < giftStruct.Length)
        {
            var (field, wire) = ReadTag(giftStruct, ref i);
            if (wire == 2)
            {
                var length = checked((int)ReadVarint(giftStruct, ref i));
                if (field == 2) return Encoding.UTF8.GetString(giftStruct, i, length);
                i += length;
            }
            else if (wire == 0)
            {
                ReadVarint(giftStruct, ref i);
            }
            else
            {
                Skip(giftStruct, ref i, wire);
            }
        }

        return string.Empty;
    }

    private static ChatMessageData? ParseEmojiChat(byte[] payload)
    {
        ulong userId = 0;
        var nick = string.Empty;
        var content = string.Empty;
        var i = 0;
        while (i < payload.Length)
        {
            var (field, wire) = ReadTag(payload, ref i);
            switch (wire)
            {
                case 0:
                    ReadVarint(payload, ref i);
                    break;
                case 2:
                {
                    var length = checked((int)ReadVarint(payload, ref i));
                    if (field == 2) (userId, nick) = ParseUser(payload.AsSpan(i, length).ToArray());
                    else if (field == 5) content = Encoding.UTF8.GetString(payload, i, length);
                    i += length;
                    break;
                }
                default:
                    Skip(payload, ref i, wire);
                    break;
            }
        }

        return new ChatMessageData(userId, string.IsNullOrWhiteSpace(nick) ? "未知用户" : nick,
            string.IsNullOrWhiteSpace(content) ? "[表情]" : content, false);
    }

    private static (ulong Id, string Nick) ParseUser(byte[] data)
    {
        ulong id = 0;
        var nick = string.Empty;
        var i = 0;
        while (i < data.Length)
        {
            var (field, wire) = ReadTag(data, ref i);
            if (wire == 0)
            {
                var value = ReadVarint(data, ref i);
                if (field == 1) id = value;
            }
            else if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref i));
                if (field == 3) nick = Encoding.UTF8.GetString(data, i, length);
                i += length;
            }
            else
            {
                Skip(data, ref i, wire);
            }
        }

        return (id, nick);
    }

    /// <summary>构造 PushFrame（心跳 payloadType=hb、应答 payloadType=ack）。</summary>
    public static byte[] BuildPushFrame(string payloadType, byte[] payload, ulong logId)
    {
        using var ms = new MemoryStream();
        if (logId != 0) WriteVarintField(ms, 2, logId);
        WriteStringField(ms, 7, payloadType);
        WriteBytesField(ms, 8, payload);
        return ms.ToArray();
    }

    /// <summary>gzip 解压 PushFrame.payload。</summary>
    public static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteTag(Stream stream, int field, int wire)
        => WriteVarint(stream, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteTag(stream, field, 0);
        WriteVarint(stream, value);
    }

    private static void WriteStringField(Stream stream, int field, string value)
        => WriteBytesField(stream, field, Encoding.UTF8.GetBytes(value));

    private static void WriteBytesField(Stream stream, int field, byte[] value)
    {
        WriteTag(stream, field, 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static (int Field, int Wire) ReadTag(byte[] data, ref int index)
    {
        var tag = (int)ReadVarint(data, ref index);
        return (tag >> 3, tag & 7);
    }

    private static ulong ReadVarint(byte[] data, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < data.Length)
        {
            var b = data[index++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Protobuf varint 过长。");
        }

        return value;
    }

    private static void Skip(byte[] data, ref int index, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(data, ref index); break;
            case 1: index += 8; break;
            case 2:
            {
                var length = checked((int)ReadVarint(data, ref index));
                index += length;
                break;
            }
            case 5: index += 4; break;
            default: index = data.Length; break;
        }
    }
}
