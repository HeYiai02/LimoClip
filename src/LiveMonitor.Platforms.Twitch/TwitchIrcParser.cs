using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch IRC（IRCv3 over WebSocket）文本协议解析器。
/// <para>
/// 一行格式：<c>[@tags] [:prefix] COMMAND [params...] [:trailing]</c>。
/// 帧可能一次携带多行（以 <c>\r\n</c> 或 <c>\n</c> 分隔），<see cref="ParseFrame"/> 逐行解析并忽略空行。
/// </para>
/// <para>
/// 解析器是无副作用的纯函数：空行/畸形行返回 null 而不是抛异常，
/// 避免单条脏数据中断整条弹幕接收循环。IRCv3 tag 值按规范反转义
/// （<c>\s</c>→空格、<c>\:</c>→分号、<c>\\</c>→反斜杠、<c>\r</c>、<c>\n</c>）。
/// </para>
/// </summary>
public static class TwitchIrcParser
{
    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    /// <summary>解析一行 IRC 文本；空行/畸形行返回 null。</summary>
    public static TwitchIrcMessage? ParseLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var text = line.AsSpan();
        if (text[^1] == '\r')
        {
            text = text[..^1];
        }

        // 容忍行首多余空格；行尾空格属于 trailing 文本的一部分，不能裁剪。
        var start = 0;
        while (start < text.Length && text[start] == ' ')
        {
            start++;
        }

        text = text[start..];
        if (text.IsWhiteSpace())
        {
            return null;
        }

        var position = 0;
        var tags = EmptyTags;

        if (text[0] == '@')
        {
            var separator = text.IndexOf(' ');
            if (separator < 0)
            {
                // 只有 tags、没有命令，无法构成有效消息。
                return null;
            }

            tags = ParseTags(text[1..separator]);
            position = separator + 1;
        }

        SkipSpaces(text, ref position);
        if (position >= text.Length)
        {
            return null;
        }

        string? prefix = null;
        if (text[position] == ':')
        {
            var separator = text[position..].IndexOf(' ');
            if (separator < 0)
            {
                // 只有 prefix、没有命令。
                return null;
            }

            prefix = text.Slice(position + 1, separator - 1).ToString();
            position += separator + 1;
        }

        SkipSpaces(text, ref position);
        if (position >= text.Length)
        {
            return null;
        }

        var commandEnd = text[position..].IndexOf(' ');
        string command;
        if (commandEnd < 0)
        {
            command = text[position..].ToString();
            position = text.Length;
        }
        else
        {
            command = text.Slice(position, commandEnd).ToString();
            position += commandEnd + 1;
        }

        if (command.Length == 0)
        {
            return null;
        }

        var parameters = new List<string>();
        while (position < text.Length)
        {
            if (text[position] == ' ')
            {
                position++;
                continue;
            }

            if (text[position] == ':')
            {
                // 最后一个 ":" 开头的参数是 trailing 文本，可包含空格。
                parameters.Add(text[(position + 1)..].ToString());
                break;
            }

            var space = text[position..].IndexOf(' ');
            if (space < 0)
            {
                parameters.Add(text[position..].ToString());
                break;
            }

            parameters.Add(text.Slice(position, space).ToString());
            position += space + 1;
        }

        return new TwitchIrcMessage(tags, prefix, command, parameters);
    }

    /// <summary>解析一帧（可能含多行），忽略空行；空帧返回空列表。</summary>
    public static IReadOnlyList<TwitchIrcMessage> ParseFrame(string? frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            return Array.Empty<TwitchIrcMessage>();
        }

        var messages = new List<TwitchIrcMessage>();
        foreach (var line in frame.Split('\n'))
        {
            var message = ParseLine(line);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages.Count == 0 ? Array.Empty<TwitchIrcMessage>() : messages;
    }

    /// <summary>
    /// 把 PRIVMSG 映射为统一的 <see cref="DanmakuMessage"/>；非 PRIVMSG 或缺少频道时返回 null。
    /// 时间戳取注入的 <paramref name="clock"/>（默认 <see cref="DateTimeOffset.UtcNow"/>）。
    /// </summary>
    public static DanmakuMessage? ToDanmakuMessage(
        TwitchIrcMessage? message,
        Func<DateTimeOffset>? clock = null)
    {
        if (message is null || !string.Equals(message.Command, "PRIVMSG", StringComparison.Ordinal))
        {
            return null;
        }

        var channel = message.Channel;
        if (string.IsNullOrEmpty(channel))
        {
            return null;
        }

        return new DanmakuMessage(
            PlatformType.Twitch,
            channel,
            message.UserId ?? string.Empty,
            message.DisplayName ?? string.Empty,
            message.Text ?? string.Empty,
            (clock ?? (static () => DateTimeOffset.UtcNow))(),
            IsGiftOrSuperChat: message.Tags.ContainsKey("bits"));
    }

    private static void SkipSpaces(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
    }

    /// <summary>
    /// 解析 <c>@tags</c> 内容（不含 '@'）：<c>key=value;key2</c>。
    /// 只有键没有等号时值为空串（如 <c>badge-info=</c>、<c>bits=</c>），键存在性本身有语义。
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseTags(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return EmptyTags;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] != ';')
            {
                continue;
            }

            var segment = text[start..index];
            start = index + 1;
            if (segment.IsEmpty)
            {
                continue;
            }

            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                tags[segment.ToString()] = string.Empty;
                continue;
            }

            var key = segment[..equals].ToString();
            if (key.Length == 0)
            {
                continue;
            }

            tags[key] = UnescapeTagValue(segment[(equals + 1)..].ToString());
        }

        return tags;
    }

    /// <summary>IRCv3 tag 值反转义；未知的 <c>\x</c> 序列按规范丢弃反斜杠保留字符。</summary>
    private static string UnescapeTagValue(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            index++;
            builder.Append(value[index] switch
            {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => value[index]
            });
        }

        return builder.ToString();
    }
}

/// <summary>
/// 一条解析后的 Twitch IRC 消息。辅助属性把频道/用户字段从 tags、prefix、参数里统一取出，
/// 供客户端直接使用而不必重复拼装。
/// </summary>
public sealed record TwitchIrcMessage(
    IReadOnlyDictionary<string, string> Tags,
    string? Prefix,
    string Command,
    IReadOnlyList<string> Parameters)
{
    /// <summary>消息所属频道名：取第一个 <c>#</c> 开头的参数，去掉 <c>#</c> 并转小写；无频道时为 null。</summary>
    public string? Channel
    {
        get
        {
            foreach (var parameter in Parameters)
            {
                if (parameter.Length > 1 && parameter[0] == '#')
                {
                    return parameter[1..].ToLowerInvariant();
                }
            }

            return null;
        }
    }

    /// <summary>发送者 login（小写）：优先 <c>login</c> tag，缺省取 prefix 里的 nick。</summary>
    public string? Login
        => Tags.TryGetValue("login", out var login) && login.Length > 0 ? login : PrefixNick;

    /// <summary>发送者展示名：优先 <c>display-name</c> tag，缺省退回 <see cref="Login"/>。</summary>
    public string? DisplayName
        => Tags.TryGetValue("display-name", out var displayName) && displayName.Length > 0
            ? displayName
            : Login;

    /// <summary>发送者数字 ID（<c>user-id</c> tag）；匿名/缺失时为 null。</summary>
    public string? UserId
        => Tags.TryGetValue("user-id", out var userId) && userId.Length > 0 ? userId : null;

    /// <summary>PRIVMSG 的 trailing 文本；其他命令或参数不足时为 null。</summary>
    public string? Text
        => string.Equals(Command, "PRIVMSG", StringComparison.Ordinal) && Parameters.Count >= 2
            ? Parameters[^1]
            : null;

    private string? PrefixNick
    {
        get
        {
            if (string.IsNullOrEmpty(Prefix))
            {
                return null;
            }

            var separator = Prefix.IndexOf('!');
            return separator > 0 ? Prefix[..separator] : Prefix;
        }
    }
}
