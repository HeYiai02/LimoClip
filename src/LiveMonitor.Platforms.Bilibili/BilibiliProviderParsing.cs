using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站 Provider 的房间号解析、画质描述与播放地址拼接等纯规则（不发起网络请求），便于直接单测。
/// </summary>
internal static class BilibiliProviderParsing
{
    private static readonly Regex HeightPattern = new(
        @"(\d{3,4})\s*P",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal sealed record Candidate(
        int Rank,
        int Qn,
        string? Host,
        string? BaseUrl,
        string? Extra,
        string? DirectUrl);

    internal sealed record QualityDescription(string? Brief, string? Detail);

    internal static bool IsNumericId(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    internal static string NormalizeNumeric(string value, string parameterName)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new ArgumentException($"无效的 B 站房间号：{value}", parameterName);
        }

        return id.ToString(CultureInfo.InvariantCulture);
    }

    internal static string? ParseRoomId(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("live.bilibili.com" or "www.bilibili.com" or "bilibili.com"))
        {
            return null;
        }

        var queryId = ParseQuery(uri.Query, "room_id") ?? ParseQuery(uri.Query, "roomid");
        if (queryId is not null && IsNumericId(queryId))
        {
            return NormalizeNumeric(queryId, nameof(uri));
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = segment.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? segment[..^5]
                : segment;
            if (IsNumericId(candidate))
            {
                return NormalizeNumeric(candidate, nameof(uri));
            }
        }

        return null;
    }

    internal static string? ParseQuery(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = pair[..separator];
            if (Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    internal static Dictionary<int, QualityDescription> ParseDescriptions(JsonElement playurl)
    {
        var result = new Dictionary<int, QualityDescription>();
        if (!playurl.TryGetProperty("g_qn_desc", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            var qn = JsonRead.Int(item, "qn");
            if (qn <= 0)
            {
                continue;
            }

            var brief = JsonRead.String(item, "desc");
            string? detail = null;
            if (item.TryGetProperty("media_base_desc", out var media)
                && media.ValueKind == JsonValueKind.Object)
            {
                if (media.TryGetProperty("brief_desc", out var briefDesc))
                {
                    brief = JsonRead.String(briefDesc, "desc") ?? brief;
                }

                if (media.TryGetProperty("detail_desc", out var detailDesc))
                {
                    detail = JsonRead.String(detailDesc, "desc");
                }
            }

            result[qn] = new QualityDescription(brief, detail);
        }

        return result;
    }

    internal static string? BuildStreamUrl(Candidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.DirectUrl)
            && Uri.TryCreate(candidate.DirectUrl, UriKind.Absolute, out _))
        {
            return candidate.DirectUrl;
        }

        if (string.IsNullOrWhiteSpace(candidate.Host) || string.IsNullOrWhiteSpace(candidate.BaseUrl))
        {
            return null;
        }

        var host = candidate.Host.TrimEnd('/');
        var baseUrl = candidate.BaseUrl;
        var url = baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : host + (baseUrl.StartsWith('/') ? baseUrl : "/" + baseUrl);

        if (!string.IsNullOrWhiteSpace(candidate.Extra))
        {
            var extra = candidate.Extra.TrimStart('&');
            if (url.Contains('?', StringComparison.Ordinal))
            {
                if (!url.EndsWith('?') && !url.EndsWith('&'))
                {
                    url += "&";
                }
            }
            else
            {
                url += "?";
            }

            url += extra;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    internal static int ParseHeight(string? description, int qn)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var match = HeightPattern.Match(description);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var height))
            {
                return height;
            }
        }

        return qn switch
        {
            30000 => 2160,
            20000 => 1440,
            15000 => 1080,
            10000 => 1080,
            400 => 1080,
            250 => 720,
            150 => 480,
            80 => 360,
            _ => 0
        };
    }

    internal static bool IsShortLinkHost(string host)
        => host == "b23.tv"
            || host.EndsWith(".b23.tv", StringComparison.Ordinal)
            || host == "bili2233.cn"
            || host.EndsWith(".bili2233.cn", StringComparison.Ordinal);
}
