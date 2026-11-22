using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// B 站直播。V0.1 只做 HTTP-FLV/HLS 状态查询与单路当前画质；
/// 需要更高画质或切换档位时，调用方传 cookie 并显式调用 <see cref="GetStreamAsync"/>。
/// </summary>
public sealed class BilibiliProvider : IPlatformProvider
{
    private const int DefaultQn = 10000;

    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _liveBase;

    public BilibiliProvider()
        : this(null)
    {
    }

    public BilibiliProvider(HttpClient? httpClient)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _apiBase = PlatformDefaults.BilibiliApiBase;
        _liveBase = PlatformDefaults.BilibiliLiveBase;
    }

    public PlatformType Platform => PlatformType.Bilibili;

    public bool CanHandle(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var value = roomUrl.Trim();
        if (IsNumericId(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (IsShortLinkHost(host))
        {
            return true;
        }

        if (host == "live.bilibili.com")
        {
            return true;
        }

        return (host == "www.bilibili.com" || host == "bilibili.com")
            && uri.AbsolutePath.StartsWith("/blackboard/live", StringComparison.OrdinalIgnoreCase);
    }

    public string ExtractRoomId(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("房间链接不能为空。", nameof(roomUrl));
        }

        var value = roomUrl.Trim();
        if (IsNumericId(value))
        {
            return NormalizeNumeric(value, nameof(roomUrl));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"无法识别的 B 站直播间链接：{roomUrl}", nameof(roomUrl));
        }

        if (IsShortLink(uri))
        {
            var resolved = ResolveShortLink(uri);
            if (string.IsNullOrWhiteSpace(resolved)
                || !Uri.TryCreate(resolved, UriKind.Absolute, out var resolvedUri)
                || IsShortLink(resolvedUri))
            {
                throw new ArgumentException($"无法解析 B 站短链：{roomUrl}", nameof(roomUrl));
            }

            return ExtractRoomId(resolved);
        }

        var parsed = ParseRoomId(uri);
        if (parsed is not null)
        {
            return parsed;
        }

        throw new ArgumentException($"无法从链接中识别 B 站直播间：{roomUrl}", nameof(roomUrl));
    }

    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        var candidate = ExtractRoomId(roomId);
        var normalizedCookie = PlatformDefaults.NormalizeCookie(cookie);

        using var roomDocument = await GetJsonAsync(
            $"{_apiBase}/room/v1/Room/get_info?room_id={Uri.EscapeDataString(candidate)}",
            normalizedCookie,
            ct).ConfigureAwait(false);

        EnsureCode(roomDocument.RootElement, "B 站房间信息");
        if (!roomDocument.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("B 站房间信息缺少 data 字段。");
        }

        var canonicalRoomId = JsonRead.String(data, "room_id");
        if (string.IsNullOrWhiteSpace(canonicalRoomId))
        {
            canonicalRoomId = candidate;
        }

        var title = JsonRead.String(data, "title") ?? string.Empty;
        var liveStatus = JsonRead.Int(data, "live_status");
        var anchorName = await GetAnchorNameAsync(canonicalRoomId, normalizedCookie, ct).ConfigureAwait(false)
            ?? $"Bilibili_{canonicalRoomId}";

        if (liveStatus == 0)
        {
            return RoomStatus.Offline(canonicalRoomId, Platform, title, anchorName);
        }

        var quality = await GetStreamAsync(canonicalRoomId, null, normalizedCookie, ct).ConfigureAwait(false);
        if (quality is null)
        {
            throw new InvalidOperationException("B 站直播接口未返回可播放的流地址。");
        }

        return new RoomStatus(
            canonicalRoomId,
            Platform,
            title,
            anchorName,
            true,
            new[] { quality });
    }

    /// <summary>
    /// 按 qn 拉取一档流。B 站游客态通常会把 qn 压到 250；传 cookie 才可能拿到 10000。
    /// 返回的 Id 为接口实际给出的 current_qn，不一定是请求值。
    /// </summary>
    public async Task<StreamQuality?> GetStreamAsync(
        string roomId,
        string? qualityId = null,
        string? cookie = null,
        CancellationToken ct = default)
    {
        var canonicalRoomId = ExtractRoomId(roomId);
        var normalizedCookie = PlatformDefaults.NormalizeCookie(cookie);
        var requestedQn = int.TryParse(qualityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : DefaultQn;

        var url = $"{_apiBase}/xlive/web-room/v2/index/getRoomPlayInfo"
            + $"?room_id={Uri.EscapeDataString(canonicalRoomId)}"
            + "&protocol=0,1&format=0,1,2&codec=0,1"
            + $"&qn={requestedQn}"
            + "&platform=web&ptype=8&dolby=5&panorama=1";

        using var document = await GetJsonAsync(url, normalizedCookie, ct).ConfigureAwait(false);
        EnsureCode(document.RootElement, "B 站播放信息");
        return ParseQuality(document.RootElement, canonicalRoomId, normalizedCookie, requestedQn);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", _liveBase + "/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"B 站接口 HTTP {(int)response.StatusCode}：{PlatformDefaults.TrimForError(body)}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"B 站接口返回了无法解析的 JSON：{PlatformDefaults.TrimForError(body)}", ex);
        }
    }

    private async Task<string?> GetAnchorNameAsync(string roomId, string? cookie, CancellationToken ct)
    {
        try
        {
            using var document = await GetJsonAsync(
                $"{_apiBase}/live_user/v1/UserInfo/get_anchor_in_room?roomid={Uri.EscapeDataString(roomId)}",
                cookie,
                ct).ConfigureAwait(false);

            if (JsonRead.Int(document.RootElement, "code") != 0)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("info", out var info))
            {
                return null;
            }

            return JsonRead.String(info, "uname");
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void EnsureCode(JsonElement root, string operation)
    {
        var code = JsonRead.Int(root, "code");
        if (code == 0)
        {
            return;
        }

        var message = JsonRead.String(root, "message") ?? string.Empty;
        throw new InvalidOperationException($"{operation}失败：code={code} {message}".Trim());
    }

    private static StreamQuality? ParseQuality(
        JsonElement root,
        string roomId,
        string? cookie,
        int requestedQn)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("playurl_info", out var playurlInfo)
            || playurlInfo.ValueKind != JsonValueKind.Object
            || !playurlInfo.TryGetProperty("playurl", out var playurl))
        {
            return null;
        }

        var descriptions = ParseDescriptions(playurl);
        Candidate? best = null;

        if (playurl.TryGetProperty("stream", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var protocol = JsonRead.String(stream, "protocol_name") ?? string.Empty;
                var protocolRank = protocol switch
                {
                    "http_stream" => 3,
                    "http_hls" => 2,
                    _ => 0
                };
                if (protocolRank == 0
                    || !stream.TryGetProperty("format", out var formats)
                    || formats.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var format in formats.EnumerateArray())
                {
                    var formatName = JsonRead.String(format, "format_name") ?? string.Empty;
                    var formatRank = formatName switch
                    {
                        "flv" => 3,
                        "ts" => 2,
                        "fmp4" => 1,
                        _ => 0
                    };
                    if (formatRank == 0
                        || !format.TryGetProperty("codec", out var codecs)
                        || codecs.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var codec in codecs.EnumerateArray())
                    {
                        var codecName = JsonRead.String(codec, "codec_name") ?? string.Empty;
                        var codecRank = codecName switch
                        {
                            "avc" => 2,
                            "hevc" => 1,
                            _ => 0
                        };
                        if (codecRank == 0
                            || !codec.TryGetProperty("url_info", out var urlInfoList)
                            || urlInfoList.ValueKind != JsonValueKind.Array
                            || urlInfoList.GetArrayLength() == 0)
                        {
                            continue;
                        }

                        var urlInfo = urlInfoList[0];
                        var host = JsonRead.String(urlInfo, "host");
                        var extra = JsonRead.String(urlInfo, "extra");
                        var directUrl = JsonRead.String(urlInfo, "url");
                        var baseUrl = JsonRead.String(codec, "base_url");
                        if (string.IsNullOrWhiteSpace(directUrl)
                            && (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(baseUrl)))
                        {
                            continue;
                        }

                        var qn = JsonRead.Int(codec, "current_qn", requestedQn);
                        if (qn <= 0)
                        {
                            qn = requestedQn;
                        }

                        var rank = (protocolRank * 100) + (formatRank * 10) + codecRank;
                        if (best is null || rank > best.Rank)
                        {
                            best = new Candidate(rank, qn, host, baseUrl, extra, directUrl);
                        }
                    }
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        var streamUrl = BuildStreamUrl(best);
        if (streamUrl is null)
        {
            return null;
        }

        descriptions.TryGetValue(best.Qn, out var description);
        var height = ParseHeight(description?.Brief, best.Qn);
        var highFrameRate = description?.Detail?.Contains("高帧率", StringComparison.Ordinal) == true
            || description?.Detail?.Contains("60", StringComparison.Ordinal) == true;
        var fps = highFrameRate ? 60 : 30;
        var displayName = string.IsNullOrWhiteSpace(description?.Brief)
            ? (height > 0 ? $"{height}P" : $"QN {best.Qn}")
            : description!.Brief!;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PlatformDefaults.UserAgent,
            ["Referer"] = $"{PlatformDefaults.BilibiliLiveBase}/{roomId}",
            ["Accept"] = "*/*"
        };
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            headers["Cookie"] = cookie;
        }

        return new StreamQuality(
            best.Qn.ToString(CultureInfo.InvariantCulture),
            displayName,
            height,
            fps,
            streamUrl,
            headers);
    }

    private static Dictionary<int, QualityDescription> ParseDescriptions(JsonElement playurl)
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

    private static string? BuildStreamUrl(Candidate candidate)
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

    private static int ParseHeight(string? description, int qn)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var match = Regex.Match(description, @"(\d{3,4})\s*P", RegexOptions.IgnoreCase);
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

    private static string? ParseRoomId(Uri uri)
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

    private static string? ParseQuery(string query, string name)
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

    private static bool IsNumericId(string value)
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

    private static string NormalizeNumeric(string value, string parameterName)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new ArgumentException($"无效的 B 站房间号：{value}", parameterName);
        }

        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsShortLink(Uri uri) => IsShortLinkHost(uri.Host.ToLowerInvariant());

    private static bool IsShortLinkHost(string host)
        => host == "b23.tv"
            || host.EndsWith(".b23.tv", StringComparison.Ordinal)
            || host == "bili2233.cn"
            || host.EndsWith(".bili2233.cn", StringComparison.Ordinal);

    private string? ResolveShortLink(Uri uri)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .GetAwaiter()
                .GetResult();
            return response.RequestMessage?.RequestUri?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record Candidate(
        int Rank,
        int Qn,
        string? Host,
        string? BaseUrl,
        string? Extra,
        string? DirectUrl);

    private sealed record QualityDescription(string? Brief, string? Detail);
}
