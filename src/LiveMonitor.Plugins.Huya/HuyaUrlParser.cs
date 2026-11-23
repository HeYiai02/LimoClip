using System.Text.RegularExpressions;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙直播间链接解析。
/// <para>
/// 支持形式：<c>https://www.huya.com/660000</c>（数字房间号）、<c>https://www.huya.com/lpl</c>（主播个性域名）、
/// <c>https://m.huya.com/...</c>、查询串形式 <c>...?roomid=660000</c>，以及纯数字房间号。
/// 个性域名不是房间号，真正的数字房间号由 <c>HuyaApiClient</c> 拉取房间页里的 <c>profileRoom</c> 解析。
/// </para>
/// </summary>
public static partial class HuyaUrlParser
{
    private static readonly string[] AllowedHosts =
    {
        "huya.com",
        "www.huya.com",
        "m.huya.com",
    };

    [GeneratedRegex(@"^\d{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoomNumberPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNamePattern();

    /// <summary>是否为纯数字房间号。</summary>
    public static bool IsRoomNumber(string? value)
        => !string.IsNullOrWhiteSpace(value) && RoomNumberPattern().IsMatch(value.Trim());

    /// <summary>是否为虎牙的个性域名形式（如 <c>lpl</c>、<c>dank1ng</c>）。</summary>
    public static bool IsHostName(string? value)
        => !string.IsNullOrWhiteSpace(value) && HostNamePattern().IsMatch(value.Trim()) && !IsRoomNumber(value);

    public static bool CanHandle(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var trimmed = roomUrl.Trim();
        if (IsRoomNumber(trimmed))
        {
            // 纯数字与 B 站短号存在歧义，虎牙只认链接；纯数字保留给内部状态查询使用。
            return false;
        }

        if (IsHostName(trimmed) && !trimmed.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return TryParseUri(trimmed, out var uri) && IsHuyaHost(uri.Host);
    }

    /// <summary>
    /// 从链接中提取房间标识：数字房间号原样返回；个性域名返回域名本身（后续由 API 解析成数字房间号）。
    /// </summary>
    public static string ExtractRoomId(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("房间链接不能为空。", nameof(roomUrl));
        }

        var trimmed = roomUrl.Trim();
        if (IsRoomNumber(trimmed))
        {
            return trimmed;
        }

        if (!TryParseUri(trimmed, out var uri) || !IsHuyaHost(uri.Host))
        {
            if (IsHostName(trimmed))
            {
                if (trimmed.Contains(':', StringComparison.Ordinal))
                {
                    throw new ArgumentException($"无法识别的虎牙房间链接：{roomUrl}", nameof(roomUrl));
                }

                return trimmed.Trim('/');
            }

            throw new ArgumentException($"无法识别的虎牙房间链接：{roomUrl}", nameof(roomUrl));
        }

        var query = uri.Query;
        if (!string.IsNullOrEmpty(query))
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2
                    && parts[0].Equals("roomid", StringComparison.OrdinalIgnoreCase)
                    && IsRoomNumber(Uri.UnescapeDataString(parts[1])))
                {
                    return Uri.UnescapeDataString(parts[1]).Trim();
                }
            }
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException($"链接中缺少虎牙房间号：{roomUrl}", nameof(roomUrl));
        }

        var room = Uri.UnescapeDataString(segments[0]).Trim('/');
        if (IsRoomNumber(room) || IsHostName(room))
        {
            return room;
        }

        throw new ArgumentException($"无法识别的虎牙房间链接：{roomUrl}", nameof(roomUrl));
    }

    /// <summary>构造虎牙房间页地址（用于个性化域名解析）。</summary>
    public static string BuildRoomPageUrl(string roomIdOrHost)
        => $"https://www.huya.com/{Uri.EscapeDataString(roomIdOrHost.Trim())}";

    private static bool TryParseUri(string value, out Uri uri)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsHuyaHost(string host)
        => AllowedHosts.Any(allowed => string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase));
}
