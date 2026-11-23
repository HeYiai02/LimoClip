using System.Net;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>抖音直播间信息快照（web_rid 为用户输入，room_id 为服务端真实房间号）。</summary>
public sealed record DouyinRoomSnapshot(
    string WebRid,
    string RoomId,
    string Title,
    string Nick,
    long OwnerUid,
    long UserCount,
    bool IsLive,
    string Ttwid,
    IReadOnlyList<StreamQuality> Qualities);

/// <summary>
/// 抖音网页版接口客户端：首页取 ttwid → webcast/room/web/enter 获取直播间状态与 FLV/HLS 拉流地址。
/// 只使用公开的网页接口，不携带登录 Cookie（匿名可用）；风控失败时返回可操作的中文错误。
/// </summary>
public sealed class DouyinApiClient : IDisposable
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public const string HomeUrl = "https://live.douyin.com/";
    public const string EnterEndpoint = "https://live.douyin.com/webcast/room/web/enter/";

    private static readonly (string Key, string DisplayName, int Height)[] ResolutionMap =
    {
        ("FULL_HD1", "原画", 1080),
        ("HD1", "高清", 720),
        ("SD1", "标清", 480),
        ("SD2", "流畅", 360),
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string? _ttwid;

    public DouyinApiClient() : this((HttpClient?)null)
    {
    }

    public DouyinApiClient(HttpClient? httpClient)
    {
        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>获取网页匿名 ttwid（抖音接口必需），带进程内缓存。</summary>
    public async Task<string> GetTtwidAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_ttwid)) return _ttwid;

        using var request = new HttpRequestMessage(HttpMethod.Get, HomeUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var ttwid = ParseTtwid(response);
        if (string.IsNullOrEmpty(ttwid))
            throw new InvalidOperationException("抖音首页未返回 ttwid，可能被风控拦截，请稍后重试或先在内置浏览器打开一次抖音直播页面。");

        _ttwid = ttwid;
        return ttwid;
    }

    /// <summary>查询直播间状态与清晰度列表。</summary>
    public async Task<DouyinRoomSnapshot> GetRoomAsync(string roomIdOrLink, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomIdOrLink))
            throw new ArgumentException("抖音直播间号不能为空。", nameof(roomIdOrLink));

        var webRid = DouyinUrlParser.TryExtractRoomId(roomIdOrLink, out var extracted)
            ? extracted
            : roomIdOrLink.Trim();
        if (!DouyinUrlParser.IsRoomNumber(webRid))
            throw new ArgumentException($"无法识别的抖音直播间号：{roomIdOrLink}", nameof(roomIdOrLink));

        var ttwid = await GetTtwidAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildEnterUrl(webRid));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Referer", DouyinUrlParser.BuildLiveUrl(webRid));
        request.Headers.TryAddWithoutValidation("Cookie", $"ttwid={ttwid}");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"抖音直播间查询失败（HTTP {(int)response.StatusCode}），请稍后重试。");

        return ParseRoom(body, webRid, ttwid);
    }

    internal static string BuildEnterUrl(string webRid) =>
        EnterEndpoint +
        "?aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN" +
        "&enter_from=web_live&cookie_enabled=true&screen_width=1920&screen_height=1080" +
        "&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=131.0.0.0" +
        "&web_rid=" + Uri.EscapeDataString(webRid);

    internal static string? ParseTtwid(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var header in values)
        {
            var index = header.IndexOf("ttwid=", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var rest = header[(index + "ttwid=".Length)..];
            var end = rest.IndexOf(';');
            return end < 0 ? rest.Trim() : rest[..end].Trim();
        }

        return null;
    }

    internal static DouyinRoomSnapshot ParseRoom(string json, string webRid, string ttwid)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var statusCode = root.TryGetProperty("status_code", out var sc) ? sc.GetInt32() : -1;
        if (statusCode != 0)
        {
            var message = root.TryGetProperty("data", out var dataElement) &&
                          dataElement.ValueKind == JsonValueKind.Object &&
                          dataElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new InvalidOperationException(
                $"抖音风控校验未通过（status_code={statusCode}{(string.IsNullOrWhiteSpace(message) ? string.Empty : "，" + message)}）。" +
                "请稍后重试；如持续失败，先在内置浏览器打开一次抖音直播间，或登录后再试。");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var rooms) ||
            rooms.ValueKind != JsonValueKind.Array ||
            rooms.GetArrayLength() == 0)
            throw new InvalidOperationException("抖音直播间不存在或已关闭。");

        var room = rooms[0];
        var roomId = room.TryGetProperty("id_str", out var idElement) ? idElement.GetString() ?? webRid : webRid;
        var status = room.TryGetProperty("status", out var statusElement) ? statusElement.GetInt32() : 4;
        var title = room.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty;
        var owner = room.TryGetProperty("owner", out var ownerElement) ? ownerElement : default;
        var nick = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("nickname", out var nickElement)
            ? nickElement.GetString() ?? string.Empty
            : string.Empty;
        var ownerUid = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("id_str", out var uidElement)
            ? ParseLong(uidElement.GetString())
            : 0;

        var live = status == 2;
        var qualities = live
            ? ParseQualities(room, webRid)
            : Array.Empty<StreamQuality>();

        return new DouyinRoomSnapshot(webRid, roomId, title, nick, ownerUid, 0, live, ttwid, qualities);
    }

    internal static IReadOnlyList<StreamQuality> ParseQualities(JsonElement room, string webRid)
    {
        var result = new List<StreamQuality>();
        if (!room.TryGetProperty("stream_url", out var streamUrl) || streamUrl.ValueKind != JsonValueKind.Object)
            return result;

        var headers = BuildStreamHeaders(webRid);
        var flv = streamUrl.TryGetProperty("flv_pull_url", out var flvElement) && flvElement.ValueKind == JsonValueKind.Object
            ? flvElement
            : default;
        var hls = streamUrl.TryGetProperty("hls_pull_url_map", out var hlsElement) && hlsElement.ValueKind == JsonValueKind.Object
            ? hlsElement
            : default;

        foreach (var (key, displayName, height) in ResolutionMap)
        {
            var url = GetUrlFromMap(flv, key) ?? GetUrlFromMap(hls, key);
            if (string.IsNullOrWhiteSpace(url)) continue;
            result.Add(new StreamQuality(
                Id: $"douyin-{key.ToLowerInvariant()}",
                DisplayName: $"{displayName} {height}P",
                Height: height,
                Fps: 30,
                StreamUrl: url,
                RequiredHeaders: headers));
        }

        return result;
    }

    private static Dictionary<string, string> BuildStreamHeaders(string webRid) => new(StringComparer.Ordinal)
    {
        ["User-Agent"] = UserAgent,
        ["Referer"] = DouyinUrlParser.BuildLiveUrl(webRid),
    };

    private static string? GetUrlFromMap(JsonElement map, string key)
        => map.ValueKind == JsonValueKind.Object &&
           map.TryGetProperty(key, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ParseLong(string? value)
        => long.TryParse(value, out var parsed) ? parsed : 0;

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
