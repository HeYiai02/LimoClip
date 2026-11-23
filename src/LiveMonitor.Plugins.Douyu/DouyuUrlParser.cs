using System.Text.RegularExpressions;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>
/// 斗鱼直播间链接与房间号解析。
/// 纯数字房间号会被 B 站 Provider 接管，因此这里只认 douyu.com 域名链接。
/// </summary>
public static partial class DouyuUrlParser
{
    private static readonly string[] AllowedHosts =
    {
        "douyu.com",
        "www.douyu.com",
        "m.douyu.com",
    };

    [GeneratedRegex(@"^\d{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoomNumberRegex();

    [GeneratedRegex(@"^\d{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathRoomRegex();

    /// <summary>判断房间号是否为纯数字。</summary>
    public static bool IsRoomNumber(string? roomId) => !string.IsNullOrWhiteSpace(roomId) && RoomNumberRegex().IsMatch(roomId.Trim());

    /// <summary>判断输入是否可以由斗鱼 Provider 处理（只认 douyu.com 链接）。</summary>
    public static bool CanHandle(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var input = roomUrl.Trim();
        if (IsRoomNumber(input))
        {
            // 纯数字优先视为 B 站房间号，避免抢占。
            return false;
        }

        if (!Uri.TryCreate(EnsureScheme(input), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>从链接中提取房间号（数字字符串）。</summary>
    public static string ExtractRoomId(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("斗鱼直播间链接为空。", nameof(roomUrl));
        }

        var input = roomUrl.Trim();
        if (IsRoomNumber(input))
        {
            return input;
        }

        if (!Uri.TryCreate(EnsureScheme(input), UriKind.Absolute, out var uri) || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"不支持的斗鱼直播间链接：{roomUrl}", nameof(roomUrl));
        }

        var roomId = ExtractRoomIdFromUri(uri);
        if (string.IsNullOrEmpty(roomId))
        {
            throw new ArgumentException($"无法从斗鱼链接中解析出房间号：{roomUrl}", nameof(roomUrl));
        }

        return roomId;
    }

    /// <summary>尝试提取房间号。</summary>
    public static bool TryExtractRoomId(string? roomUrl, out string? roomId)
    {
        roomId = null;
        if (!CanHandle(roomUrl))
        {
            return false;
        }

        try
        {
            roomId = ExtractRoomId(roomUrl!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ExtractRoomIdFromUri(Uri uri)
    {
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (PathRoomRegex().IsMatch(segment))
            {
                return segment;
            }
        }

        return null;
    }

    private static string EnsureScheme(string input) =>
        input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
}
