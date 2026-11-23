using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站直播弹幕二进制协议解析器。
/// <para>
/// 帧结构：16 字节大端包头 <c>[len4][headerLen2][protoVer2][op4][seq4]</c>，
/// 其后是可能出现多个粘包/压缩负载的包体。protoVer 为 2（zlib）或 3（brotli）时
/// 解压后递归解析，深度上限 <see cref="MaxDecompressDepth"/>。
/// </para>
/// <para>
/// 解析器是无副作用的纯函数：畸形包、未知包、无法解压的包一律静默跳过，绝不抛异常，
/// 避免单条脏数据中断整条弹幕接收循环。时间戳一律取注入的 <paramref name="clock"/>，
/// 不使用平台服务端时间。
/// </para>
/// </summary>
public static class BilibiliPacketParser
{
    /// <summary>包头固定长度。</summary>
    public const int HeaderSize = 16;

    /// <summary>协议版本：普通 JSON。</summary>
    public const short ProtoJson = 0;

    /// <summary>协议版本：非压缩负载（心跳回执、认证回执等）。</summary>
    public const short ProtoHeartbeat = 1;

    /// <summary>协议版本：zlib 压缩。</summary>
    public const short ProtoZlib = 2;

    /// <summary>协议版本：brotli 压缩。</summary>
    public const short ProtoBrotli = 3;

    /// <summary>操作码：客户端心跳。</summary>
    public const int OpClientHeartbeat = 2;

    /// <summary>操作码：心跳回执。</summary>
    public const int OpHeartbeatReply = 3;

    /// <summary>操作码：业务通知（JSON 或压缩的多个包）。</summary>
    public const int OpNotification = 5;

    /// <summary>操作码：客户端认证。</summary>
    public const int OpClientAuth = 7;

    /// <summary>操作码：认证回执。</summary>
    public const int OpAuthReply = 8;

    /// <summary>压缩负载最大递归深度，防止畸形/恶意包无限嵌套。</summary>
    public const int MaxDecompressDepth = 4;

    /// <summary>单个压缩包解压后的体积上限，防止压缩炸弹。</summary>
    private const int MaxDecompressedBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>构造一个协议包（16 字节大端头 + UTF-8 负载）。</summary>
    /// <param name="operation">操作码，见 <see cref="OpNotification"/> 等常量。</param>
    /// <param name="bodyText">包体文本；null/空串表示空包体。</param>
    /// <param name="protoVersion">包头里的协议版本，默认 1。</param>
    /// <param name="sequenceId">序号，默认 1。</param>
    public static byte[] BuildPacket(
        int operation,
        string? bodyText,
        short protoVersion = ProtoHeartbeat,
        int sequenceId = 1)
    {
        if (string.IsNullOrEmpty(bodyText))
        {
            return BuildPacket(operation, ReadOnlySpan<byte>.Empty, protoVersion, sequenceId);
        }

        var body = Encoding.UTF8.GetBytes(bodyText);
        return BuildPacket(operation, body.AsSpan(), protoVersion, sequenceId);
    }

    /// <summary>构造一个协议包（16 字节大端头 + 二进制负载）。</summary>
    public static byte[] BuildPacket(
        int operation,
        ReadOnlySpan<byte> body,
        short protoVersion = ProtoHeartbeat,
        int sequenceId = 1)
    {
        var totalLength = HeaderSize + body.Length;
        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteInt32BigEndian(span[..4], totalLength);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(4, 2), (short)HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(6, 2), protoVersion);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(12, 4), sequenceId);
        body.CopyTo(span[HeaderSize..]);

        return buffer;
    }

    /// <summary>
    /// 构造 op=7 进房认证包。包头 protoVer 固定为 1，包体 JSON 里 protover 为 3（请求服务端压缩下发）。
    /// </summary>
    public static byte[] BuildAuthPacket(long roomId, long uid, string? token, string? buvid)
    {
        var payload = new AuthPayload(
            uid,
            roomId,
            3,
            buvid ?? string.Empty,
            "web",
            2,
            token ?? string.Empty);
        return BuildPacket(OpClientAuth, JsonSerializer.Serialize(payload), ProtoHeartbeat);
    }

    /// <summary>构造 op=2 心跳包，包体固定为 <c>[object Object]</c>。</summary>
    public static byte[] BuildHeartbeatPacket()
        => BuildPacket(OpClientHeartbeat, "[object Object]", ProtoHeartbeat);

    /// <summary>
    /// 解析一帧（可能包含多个粘连包与多层压缩包），返回弹幕消息及认证/心跳回执识别结果。
    /// 畸形包静默跳过，不会抛异常。
    /// </summary>
    /// <param name="frame">WebSocket 收到的一帧完整二进制数据。</param>
    /// <param name="roomId">消息归属的本地房间号（写入 <see cref="DanmakuMessage.RoomId"/>）。</param>
    /// <param name="clock">时间戳来源，默认 <see cref="DateTimeOffset.UtcNow"/>。</param>
    public static BilibiliParseResult ParseFrame(
        ReadOnlyMemory<byte> frame,
        string roomId,
        Func<DateTimeOffset>? clock = null)
    {
        var state = new ParseState(roomId, clock ?? (static () => DateTimeOffset.UtcNow));
        ParsePackets(frame, state, depth: 0);
        return state.BuildResult();
    }

    /// <summary>解析一帧并只返回弹幕消息（认证/心跳回执信息被忽略）。</summary>
    public static IReadOnlyList<DanmakuMessage> Parse(
        ReadOnlyMemory<byte> frame,
        string roomId,
        Func<DateTimeOffset>? clock = null)
        => ParseFrame(frame, roomId, clock).Messages;

    private static void ParsePackets(ReadOnlyMemory<byte> frame, ParseState state, int depth)
    {
        if (depth > MaxDecompressDepth || frame.Length < HeaderSize)
        {
            return;
        }

        var span = frame.Span;
        var offset = 0;
        while (offset + HeaderSize <= frame.Length)
        {
            var packetLength = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset, 4));
            var headerLength = BinaryPrimitives.ReadInt16BigEndian(span.Slice(offset + 4, 2));
            var protoVersion = BinaryPrimitives.ReadInt16BigEndian(span.Slice(offset + 6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset + 8, 4));

            if (packetLength < HeaderSize
                || headerLength < HeaderSize
                || packetLength > frame.Length - offset)
            {
                // 帧被截断或长度非法，剩余字节无法可靠切分，停止。
                break;
            }

            var body = frame.Slice(offset + headerLength, packetLength - headerLength);
            try
            {
                HandlePacket(operation, protoVersion, body, state, depth);
            }
            catch (Exception)
            {
                // 单个包畸形不影响同一帧里的其他包。
            }

            offset += packetLength;
        }
    }

    private static void HandlePacket(
        int operation,
        short protoVersion,
        ReadOnlyMemory<byte> body,
        ParseState state,
        int depth)
    {
        switch (operation)
        {
            case OpAuthReply:
                state.ReadAuthReply(body);
                return;

            case OpHeartbeatReply:
                state.HeartbeatReplyReceived = true;
                return;

            case OpNotification:
                switch (protoVersion)
                {
                    case ProtoZlib:
                    case ProtoBrotli:
                        var decompressed = TryDecompress(body, protoVersion);
                        if (decompressed is not null)
                        {
                            ParsePackets(decompressed.Value, state, depth + 1);
                        }

                        return;

                    default:
                        state.ReadNotification(body);
                        return;
                }

            default:
                // 未知/客户端单向操作码（op=2、op=7 等）静默跳过。
                return;
        }
    }

    private static ReadOnlyMemory<byte>? TryDecompress(ReadOnlyMemory<byte> body, short protoVersion)
    {
        try
        {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using Stream decompressor = protoVersion == ProtoZlib
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecompressedBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return new ReadOnlyMemory<byte>(output.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ParseState
    {
        private readonly List<DanmakuMessage> _messages = new();

        public ParseState(string roomId, Func<DateTimeOffset> clock)
        {
            RoomId = roomId;
            Clock = clock;
        }

        public string RoomId { get; }

        public Func<DateTimeOffset> Clock { get; }

        public bool AuthReplyReceived { get; private set; }

        public int AuthReplyCode { get; private set; }

        public string? AuthReplyMessage { get; private set; }

        public bool HeartbeatReplyReceived { get; set; }

        public void ReadAuthReply(ReadOnlyMemory<byte> body)
        {
            AuthReplyReceived = true;
            try
            {
                using var document = JsonDocument.Parse(body, JsonOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AuthReplyCode = JsonRead.Int(document.RootElement, "code");
                    AuthReplyMessage = JsonRead.String(document.RootElement, "message");
                    return;
                }
            }
            catch (JsonException)
            {
                // 落到下面按认证失败处理，避免把无法解析的回执当成成功。
            }

            AuthReplyCode = -1;
            AuthReplyMessage = "无法解析的认证回执";
        }

        public void ReadNotification(ReadOnlyMemory<byte> body)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body, JsonOptions);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var command = JsonRead.String(root, "cmd");
                if (string.IsNullOrEmpty(command))
                {
                    return;
                }

                // 兼容 DANMU_MSG:4:0:2:2:2:0 等带后缀的变体。
                if (command.StartsWith("DANMU_MSG", StringComparison.Ordinal))
                {
                    AppendDanmaku(root);
                    return;
                }

                // SEND_GIFT / SUPER_CHAT_MESSAGE 精确匹配，避免 *JPN 重复包与 *_DELETE 撤回包。
                switch (command)
                {
                    case "SEND_GIFT":
                        AppendGift(root);
                        return;
                    case "SUPER_CHAT_MESSAGE":
                        AppendSuperChat(root);
                        return;
                    default:
                        return;
                }
            }
        }

        public BilibiliParseResult BuildResult()
            => new(
                _messages.Count == 0 ? Array.Empty<DanmakuMessage>() : _messages,
                AuthReplyReceived,
                AuthReplyCode,
                AuthReplyMessage,
                HeartbeatReplyReceived);

        private void AppendDanmaku(JsonElement root)
        {
            if (!root.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Array
                || info.GetArrayLength() < 3)
            {
                return;
            }

            // info[1] 是弹幕文本，info[2] 是 [uid, uname, ...]。
            var content = ReadText(info[1]);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var senderUid = "0";
            var senderName = string.Empty;
            var user = info[2];
            if (user.ValueKind == JsonValueKind.Array && user.GetArrayLength() >= 2)
            {
                senderUid = ReadText(user[0]) ?? "0";
                senderName = ReadText(user[1]) ?? string.Empty;
            }

            _messages.Add(new DanmakuMessage(
                PlatformType.Bilibili,
                RoomId,
                senderUid,
                senderName,
                content,
                Clock(),
                IsGiftOrSuperChat: false));
        }

        private void AppendGift(JsonElement root)
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var giftName = JsonRead.String(data, "giftName");
            if (string.IsNullOrWhiteSpace(giftName))
            {
                return;
            }

            var count = JsonRead.Int(data, "num", 1);
            var content = count > 1 ? $"{giftName} x{count}" : giftName;
            _messages.Add(new DanmakuMessage(
                PlatformType.Bilibili,
                RoomId,
                ReadId(data, "uid"),
                JsonRead.String(data, "uname") ?? string.Empty,
                content,
                Clock(),
                IsGiftOrSuperChat: true));
        }

        private void AppendSuperChat(JsonElement root)
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var senderName = string.Empty;
            if (data.TryGetProperty("user_info", out var userInfo) && userInfo.ValueKind == JsonValueKind.Object)
            {
                senderName = JsonRead.String(userInfo, "uname") ?? string.Empty;
            }

            if (senderName.Length == 0)
            {
                senderName = JsonRead.String(data, "uname") ?? string.Empty;
            }

            _messages.Add(new DanmakuMessage(
                PlatformType.Bilibili,
                RoomId,
                ReadId(data, "uid"),
                senderName,
                JsonRead.String(data, "message") ?? string.Empty,
                Clock(),
                IsGiftOrSuperChat: true));
        }

        private static string? ReadText(JsonElement element)
            => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            };

        private static string ReadId(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return "0";
            }

            var text = ReadText(value);
            return string.IsNullOrEmpty(text) ? "0" : text;
        }

    }

    /// <summary>op=7 认证包体（字段名/顺序与 B 站网页端一致）。</summary>
    private sealed record AuthPayload(
        long uid,
        long roomid,
        int protover,
        string buvid,
        string platform,
        int type,
        string key);
}

/// <summary>一帧二进制数据的解析结果。</summary>
/// <param name="Messages">解析出的弹幕/礼物/SC 消息。</param>
/// <param name="AuthReplyReceived">是否出现 op=8 认证回执。</param>
/// <param name="AuthReplyCode">认证回执里的 code；0 表示成功。</param>
/// <param name="AuthReplyMessage">认证回执里的 message。</param>
/// <param name="HeartbeatReplyReceived">是否出现 op=3 心跳回执。</param>
public readonly record struct BilibiliParseResult(
    IReadOnlyList<DanmakuMessage> Messages,
    bool AuthReplyReceived,
    int AuthReplyCode,
    string? AuthReplyMessage,
    bool HeartbeatReplyReceived);
