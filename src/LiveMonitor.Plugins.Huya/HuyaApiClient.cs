using System.Text;
using System.Text.Json;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Huya;

/// <summary>虎牙 FLV 拉流线路。</summary>
internal sealed record HuyaStreamLine(string CdnType, int LineIndex, int WebPriorityRate, string Url);

/// <summary>虎牙房间页返回的一档清晰度（仅名称/码率元数据；真实拉流地址由当前线路决定）。</summary>
internal sealed record HuyaBitRateInfo(string DisplayName, int BitRate);

/// <summary>虎牙房间当前状态快照。</summary>
internal sealed record HuyaRoomSnapshot(
    string RoomId,
    long PresenterUid,
    string Nick,
    string RoomName,
    bool IsLive,
    IReadOnlyList<HuyaStreamLine> FlvLines,
    IReadOnlyList<HuyaBitRateInfo> BitRates,
    int MaxBitRate);

/// <summary>
/// 虎牙移动端房间接口访问器。
/// <para>
/// 房间状态/拉流地址取 <c>https://mp.huya.com/cache.php?m=Live&amp;do=profileRoom&amp;roomid=...&amp;showSecret=1</c>：
/// 返回干净 JSON，含 <c>data.profileInfo.uid</c>（弹幕分组要用）、<c>data.liveData</c>（标题/昵称/清晰度档位）、
/// <c>data.stream.flv.multiLine</c>（带 wsSecret 签名的 FLV 地址）。
/// 主播个性域名（如 <c>huya.com/lpl</c>）不是房间号，需要额外抓一次房间页从 <c>hyPlayerConfig</c> 的
/// <c>"profileRoom":&lt;数字&gt;</c> 解析；页面为 gzip 压缩，这里手动解压。
/// 响应解析规则集中在 <see cref="HuyaApiClientParsing"/>（纯函数，直接单测），本类只保留 HTTP 编排。
/// </para>
/// </summary>
internal sealed class HuyaApiClient : IDisposable
{
    private const string MobileProfileEndpoint =
        "https://mp.huya.com/cache.php?m=Live&do=profileRoom&showSecret=1&roomid={0}";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _hostRoomCache =
        new(StringComparer.OrdinalIgnoreCase);

    public HuyaApiClient(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    /// <summary>查询房间状态；<paramref name="roomIdOrHost"/> 可以是数字房间号或个性域名。</summary>
    public Task<HuyaRoomSnapshot> GetRoomAsync(string roomIdOrHost, CancellationToken ct)
        => GetRoomAsync(roomIdOrHost, cookie: null, ct);

    /// <summary>
    /// 查询房间状态；<paramref name="cookie"/> 为该平台登录 Cookie。
    /// 带登录态时虎牙可能下发更多线路/档位（高码率档位通常要求登录），匿名态下照常可用。
    /// </summary>
    public async Task<HuyaRoomSnapshot> GetRoomAsync(string roomIdOrHost, string? cookie, CancellationToken ct)
    {
        var token = (roomIdOrHost ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            throw new ArgumentException("虎牙房间号不能为空。", nameof(roomIdOrHost));
        }

        var roomNumber = HuyaUrlParser.IsRoomNumber(token)
            ? token
            : await ResolveRoomNumberAsync(token, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(MobileProfileEndpoint, Uri.EscapeDataString(roomNumber)));
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", $"https://www.huya.com/{roomNumber}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        var safeCookie = NormalizeCookie(cookie);
        if (safeCookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", safeCookie);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var status = HuyaApiClientParsing.ReadStatus(root);
        var message = HuyaApiClientParsing.ReadMessage(root);

        if (status != 200)
        {
            throw new InvalidOperationException(status == 422
                ? "虎牙房间不存在或主播已注销（422）。"
                : $"虎牙房间接口返回错误（status={status}）：{message}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("虎牙房间接口返回数据不完整（缺少 data）。");
        }

        var liveData = data.TryGetProperty("liveData", out var liveDataElement) && liveDataElement.ValueKind == JsonValueKind.Object
            ? liveDataElement
            : default;
        var profileInfo = data.TryGetProperty("profileInfo", out var profileInfoElement) && profileInfoElement.ValueKind == JsonValueKind.Object
            ? profileInfoElement
            : default;

        var isLive = data.TryGetProperty("liveStatus", out var liveStatus) && liveStatus.ValueKind == JsonValueKind.String
            && string.Equals(liveStatus.GetString(), "ON", StringComparison.OrdinalIgnoreCase);

        var presenterUid = HuyaApiClientParsing.ReadInt64(profileInfo, "uid");
        if (presenterUid <= 0)
        {
            presenterUid = HuyaApiClientParsing.ReadInt64(liveData, "uid");
        }

        var nick = HuyaApiClientParsing.ReadString(profileInfo, "nick");
        if (string.IsNullOrWhiteSpace(nick))
        {
            nick = HuyaApiClientParsing.ReadString(liveData, "nick");
        }

        var roomName = HuyaApiClientParsing.ReadString(liveData, "roomName");
        if (string.IsNullOrWhiteSpace(roomName))
        {
            roomName = nick;
        }

        var canonicalRoomId = HuyaApiClientParsing.ReadInt64(liveData, "profileRoom");
        if (canonicalRoomId <= 0)
        {
            canonicalRoomId = HuyaApiClientParsing.ReadInt64(profileInfo, "profileRoom");
        }

        var flvLines = HuyaApiClientParsing.ParseFlvLines(data);
        var bitRates = HuyaApiClientParsing.ParseBitRates(liveData);
        var maxBitRate = (int)HuyaApiClientParsing.ReadInt64(liveData, "bitRate");

        return new HuyaRoomSnapshot(
            canonicalRoomId > 0 ? canonicalRoomId.ToString() : roomNumber,
            presenterUid,
            nick,
            roomName,
            isLive,
            flvLines,
            bitRates,
            maxBitRate);
    }

    /// <summary>清洗 Cookie：去掉换行，空值返回 null（防止请求头注入）。</summary>
    internal static string? NormalizeCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var trimmed = cookie.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>把个性域名解析成数字房间号；结果按域名缓存。</summary>
    private async Task<string> ResolveRoomNumberAsync(string host, CancellationToken ct)
    {
        if (_hostRoomCache.TryGetValue(host, out var cached))
        {
            return cached;
        }

        var pageUrl = HuyaUrlParser.BuildRoomPageUrl(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.huya.com/");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var html = HuyaApiClientParsing.DecodeContent(bytes, response.Content.Headers.ContentEncoding);

        var roomNumber = HuyaApiClientParsing.ExtractProfileRoom(html);
        if (roomNumber is null)
        {
            throw new InvalidOperationException(
                "无法从虎牙房间页解析出数字房间号，请改用数字房间号链接（如 https://www.huya.com/660000）。");
        }

        _hostRoomCache[host] = roomNumber;
        return roomNumber;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
