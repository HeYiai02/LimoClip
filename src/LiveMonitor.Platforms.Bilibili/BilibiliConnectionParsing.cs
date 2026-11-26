using System.Globalization;
using System.Text.Json;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站弹幕连接流程中的纯解析规则（wbi 密钥提取、弹幕服务器地址选择、Cookie 取值等），
/// 不发起网络请求、不持有状态，便于直接单测。
/// </summary>
internal static class BilibiliConnectionParsing
{
    internal readonly record struct HostCandidate(string Host, int WssPort, int WsPort, int Port);

    internal static string? ExtractWbiKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        var slash = trimmed.LastIndexOf('/');
        var fileName = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        var dot = fileName.IndexOf('.');
        if (dot > 0)
        {
            fileName = fileName[..dot];
        }

        return fileName.Length == 0 ? null : fileName;
    }

    internal static Uri? SelectHostUri(JsonElement hostList)
    {
        var candidates = new List<HostCandidate>();
        foreach (var entry in hostList.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var host = JsonRead.String(entry, "host");
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            candidates.Add(new HostCandidate(
                host.Trim(),
                JsonRead.Int(entry, "wss_port"),
                JsonRead.Int(entry, "ws_port"),
                JsonRead.Int(entry, "port")));
        }

        foreach (var candidate in candidates)
        {
            if (candidate.WssPort > 0 && TryBuildWebSocketUri(candidate.Host, candidate.WssPort, out var uri))
            {
                return uri;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.WsPort > 0 && TryBuildWebSocketUri(candidate.Host, candidate.WsPort, out var uri))
            {
                return uri;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Port > 0 && TryBuildWebSocketUri(candidate.Host, candidate.Port, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    internal static bool TryBuildWebSocketUri(string host, int port, out Uri? uri)
    {
        var normalized = host.Trim();
        if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[6..];
        }
        else if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[8..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        normalized = normalized.TrimEnd('/');
        var url = $"wss://{normalized}:{port.ToString(CultureInfo.InvariantCulture)}/sub";
        return Uri.TryCreate(url, UriKind.Absolute, out uri);
    }

    internal static string? GetCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        foreach (var pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = pair.Trim();
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!segment.AsSpan(0, separator).Trim().Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            var value = segment[(separator + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    internal static long ParseOwnerUid(string? cookie)
    {
        var raw = GetCookieValue(cookie, "DedeUserID");
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid > 0
            ? uid
            : 0;
    }
}
