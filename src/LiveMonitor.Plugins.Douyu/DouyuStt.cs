using System.Text;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>
/// 斗鱼弹幕 TCP 协议（STT）编解码。
/// 报文头 12 字节：消息长度(LE int32，= 数据长度+8)、重复长度、消息类型(LE int16)、加密字节、保留字节；
/// 数据部分为 UTF-8 STT 文本，结尾必须为 '\0'。689=客户端文本，690=服务端文本。
/// </summary>
internal static class DouyuStt
{
    public const short ClientTextType = 689;
    public const short ServerTextType = 690;

    public static byte[] BuildPacket(string payload, short type = ClientTextType)
    {
        var normalized = payload.EndsWith('\0') ? payload : payload + "\0";
        var body = Encoding.UTF8.GetBytes(normalized);
        var length = body.Length + 8;
        var buffer = new byte[body.Length + 12];
        BitConverter.GetBytes(length).CopyTo(buffer, 0);
        BitConverter.GetBytes(length).CopyTo(buffer, 4);
        BitConverter.GetBytes(type).CopyTo(buffer, 8);
        buffer[10] = 0;
        buffer[11] = 0;
        body.CopyTo(buffer, 12);
        return buffer;
    }

    /// <summary>解析完整报文（调用方已按长度读取整包）。</summary>
    public static bool TryParsePacket(byte[] buffer, int length, out short type, out string payload)
    {
        type = 0;
        payload = string.Empty;
        if (buffer is null || length < 12)
        {
            return false;
        }

        var declared = BitConverter.ToInt32(buffer, 0);
        if (declared <= 8 || declared > 1_000_000 || declared + 4 != length)
        {
            return false;
        }

        type = BitConverter.ToInt16(buffer, 8);
        var payloadLength = declared - 8;
        if (payloadLength < 0 || payloadLength > length - 12)
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(buffer, 12, payloadLength);
        return payload.Length > 0;
    }

    /// <summary>解析 STT 序列化文本：key@=value/key@=value/；转义 @S=>'/'、@A=>'@'。</summary>
    public static Dictionary<string, string> ParseStt(string payload)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in payload.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = segment.IndexOf("@=", StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var key = Unescape(segment[..index]);
            var value = Unescape(segment[(index + 2)..]);
            result[key] = value;
        }

        return result;
    }

    public static string Escape(string value) => value.Replace("@", "@A").Replace("/", "@S");

    public static string Unescape(string value) => value.Replace("@S", "/").Replace("@A", "@");
}
