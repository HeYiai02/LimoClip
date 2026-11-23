using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    IReadOnlyList<HuyaBitRateInfo> BitRates);

/// <summary>
/// 虎牙移动端房间接口访问器。
/// <para>
/// 房间状态/拉流地址取 <c>https://mp.huya.com/cache.php?m=Live&amp;do=profileRoom&amp;roomid=...&amp;showSecret=1</c>：
/// 返回干净 JSON，含 <c>data.profileInfo.uid</c>（弹幕分组要用）、<c>data.liveData</c>（标题/昵称/清晰度档位）、
/// <c>data.stream.flv.multiLine</c>（带 wsSecret 签名的 FLV 地址）。
/// 主播个性域名（如 <c>huya.com/lpl</c>）不是房间号，需要额外抓一次房间页从 <c>hyPlayerConfig</c> 的
/// <c>"profileRoom":&lt;数字&gt;</c> 解析；页面为 gzip 压缩，这里手动解压。
/// </para>
/// </summary>
internal sealed partial class HuyaApiClient : IDisposable
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

    [GeneratedRegex(@"""profileRoom""\s*:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileRoomPattern();

    /// <summary>查询房间状态；<paramref name="roomIdOrHost"/> 可以是数字房间号或个性域名。</summary>
    public async Task<HuyaRoomSnapshot> GetRoomAsync(string roomIdOrHost, CancellationToken ct)
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

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Number
            ? statusElement.GetInt32()
            : 0;
        var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? string.Empty
            : string.Empty;

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

        var presenterUid = ReadInt64(profileInfo, "uid");
        if (presenterUid <= 0)
        {
            presenterUid = ReadInt64(liveData, "uid");
        }

        var nick = ReadString(profileInfo, "nick");
        if (string.IsNullOrWhiteSpace(nick))
        {
            nick = ReadString(liveData, "nick");
        }

        var roomName = ReadString(liveData, "roomName");
        if (string.IsNullOrWhiteSpace(roomName))
        {
            roomName = nick;
        }

        var canonicalRoomId = ReadInt64(liveData, "profileRoom");
        if (canonicalRoomId <= 0)
        {
            canonicalRoomId = ReadInt64(profileInfo, "profileRoom");
        }

        var flvLines = ParseFlvLines(data);
        var bitRates = ParseBitRates(liveData);

        return new HuyaRoomSnapshot(
            canonicalRoomId > 0 ? canonicalRoomId.ToString() : roomNumber,
            presenterUid,
            nick,
            roomName,
            isLive,
            flvLines,
            bitRates);
    }

    private static IReadOnlyList<HuyaStreamLine> ParseFlvLines(JsonElement data)
    {
        if (!data.TryGetProperty("stream", out var stream)
            || stream.ValueKind != JsonValueKind.Object
            || !stream.TryGetProperty("flv", out var flv)
            || flv.ValueKind != JsonValueKind.Object
            || !flv.TryGetProperty("multiLine", out var multiLine)
            || multiLine.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HuyaStreamLine>();
        }

        var lines = new List<HuyaStreamLine>();
        foreach (var item in multiLine.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            lines.Add(new HuyaStreamLine(
                ReadString(item, "cdnType"),
                (int)ReadInt64(item, "lineIndex"),
                (int)ReadInt64(item, "webPriorityRate"),
                url));
        }

        return lines;
    }

    private static IReadOnlyList<HuyaBitRateInfo> ParseBitRates(JsonElement liveData)
    {
        var raw = ReadString(liveData, "bitRateInfo");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<HuyaBitRateInfo>();
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<HuyaBitRateInfo>();
            }

            var result = new List<HuyaBitRateInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(item, "sDisplayName");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result.Add(new HuyaBitRateInfo(name, (int)ReadInt64(item, "iBitRate")));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<HuyaBitRateInfo>();
        }
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
        var html = DecodeContent(bytes, response.Content.Headers.ContentEncoding);

        var match = ProfileRoomPattern().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "无法从虎牙房间页解析出数字房间号，请改用数字房间号链接（如 https://www.huya.com/660000）。");
        }

        var roomNumber = match.Groups[1].Value;
        _hostRoomCache[host] = roomNumber;
        return roomNumber;
    }

    private static string DecodeContent(byte[] bytes, ICollection<string> contentEncodings)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = contentEncodings.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (encoding is "gzip" || bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (encoding == "deflate")
        {
            using var deflate = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (encoding == "br")
        {
            using var brotli = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal static string ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static long ReadInt64(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : 0;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
