using System.Text;

namespace LiveMonitor.Plugins.Huya;

/// <summary>一条解析出来的虎牙弹幕。</summary>
internal sealed record HuyaChatMessage(string SenderUid, string SenderName, string Content);

/// <summary>
/// 虎牙弹幕报文编解码（网页版 webh5 信令）。
/// <para>
/// 连接 <c>wss://wsapi.huya.com/?baseinfo=...</c> 后：命令 16 订阅消息组 <c>live:&lt;主播uid&gt;</c> / <c>chat:&lt;主播uid&gt;</c>；
/// 服务端以命令 7（单条）与命令 22（按组批量）推送，消息体用 uri 区分：1400 弹幕、8006 在线人数。
/// 旧的 <c>wss://cdnws.api.huya.com</c> + 命令 1 进房协议已半废弃（连上仍会推十几秒后静默），不再使用。
/// </para>
/// </summary>
internal static class HuyaPacketParser
{
    public const int CmdPush = 7;
    public const int CmdSubscribe = 16;
    public const int CmdGroupPush = 22;

    public const int UriChat = 1400;
    public const int UriOnline = 8006;

    private const string WebH5UserAgent = "webh5&2608121011&websocket";
    private const string Locale = "HUYA&ZH&2052";

    /// <summary>构造命令 16 的订阅包。</summary>
    public static byte[] BuildSubscribe(ulong presenterUid)
    {
        var payload = new HuyaTarsWriter();
        payload.WriteStringList(new[] { $"live:{presenterUid}", $"chat:{presenterUid}" }, 0);
        payload.Write(string.Empty, 1);

        var frame = new HuyaTarsWriter();
        frame.Write(CmdSubscribe, 0);
        frame.Write(payload.ToArray(), 1);
        return frame.ToArray();
    }

    /// <summary>构造带 baseinfo 的 WebSocket 连接地址。</summary>
    public static string BuildSocketUrl(string guid)
    {
        var writer = new HuyaTarsWriter();
        writer.Write(0, 0);
        writer.Write(guid, 1);
        writer.Write(WebH5UserAgent, 2);
        writer.Write(Locale, 3);
        writer.Write(string.Empty, 4);
        writer.Write(string.Empty, 5);
        writer.Write(0, 6);
        writer.Write(string.Empty, 7);
        writer.Write(string.Empty, 8);
        writer.Write(string.Empty, 9);

        var baseInfo = Convert.ToBase64String(writer.ToArray());
        return $"wss://wsapi.huya.com/?baseinfo={Uri.EscapeDataString(baseInfo)}";
    }

    /// <summary>生成 32 位小写十六进制 guid（客户端标识；服务端不校验真实性）。</summary>
    public static string CreateGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);
        var builder = new StringBuilder(32);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }

    /// <summary>解析一帧推送；返回 false 表示帧格式不合法（忽略即可，不影响后续接收）。</summary>
    public static bool TryParse(byte[] frame, out List<HuyaChatMessage> messages)
    {
        messages = new List<HuyaChatMessage>();
        if (frame is null || frame.Length == 0)
        {
            return false;
        }

        try
        {
            var reader = new HuyaTarsReader(frame);
            var command = reader.ReadInt(0);
            var payload = reader.ReadBytes(1);
            if (payload.Length == 0)
            {
                return true;
            }

            switch (command)
            {
                case CmdPush:
                    ParsePush(payload, messages);
                    break;
                case CmdGroupPush:
                    ParseGroupPush(payload, messages);
                    break;
                default:
                    // 订阅结果（17）/其它服务端通知与弹幕无关，直接忽略。
                    break;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ParsePush(byte[] payload, List<HuyaChatMessage> messages)
    {
        var reader = new HuyaTarsReader(payload);
        reader.ReadInt(0); // pushType
        var uri = reader.ReadInt(1);
        var body = reader.ReadBytes(2);
        Dispatch(uri, body, messages);
    }

    private static void ParseGroupPush(byte[] payload, List<HuyaChatMessage> messages)
    {
        var reader = new HuyaTarsReader(payload);
        reader.ReadString(0); // 组名
        var count = reader.EnterList(1);
        for (var i = 0; i < count; i++)
        {
            if (!reader.EnterStructElement())
            {
                break;
            }

            var uri = reader.ReadInt(0);
            var body = reader.ReadBytes(1);
            reader.EndStruct();
            Dispatch(uri, body, messages);
        }
    }

    private static void Dispatch(long uri, byte[] body, List<HuyaChatMessage> messages)
    {
        if (uri != UriChat || body.Length == 0)
        {
            return;
        }

        if (TryParseChat(body, out var chat))
        {
            messages.Add(chat);
        }
    }

    /// <summary>
    /// uri 1400 消息体：tag0 发送者结构（tag0 uid / tag2 昵称 / tag4 头像）、tag3 弹幕内容、tag6 弹幕格式（tag0 字体颜色）。
    /// </summary>
    private static bool TryParseChat(byte[] body, out HuyaChatMessage message)
    {
        message = new HuyaChatMessage(string.Empty, string.Empty, string.Empty);
        var reader = new HuyaTarsReader(body);
        var uid = 0L;
        var nick = string.Empty;
        if (reader.EnterStruct(0))
        {
            uid = reader.ReadInt(0);
            nick = reader.ReadString(2);
            reader.EndStruct();
        }

        var content = reader.ReadString(3);
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        message = new HuyaChatMessage(
            uid > 0 ? uid.ToString() : "0",
            string.IsNullOrWhiteSpace(nick) ? "未知用户" : nick,
            content);
        return true;
    }
}
