using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>斗鱼房间快照。</summary>
public sealed record DouyuRoomSnapshot(
    string RoomId,
    string RoomName,
    string Nick,
    long OwnerUid,
    bool IsLive,
    IReadOnlyList<StreamQuality> Qualities);

/// <summary>
/// 斗鱼 Web 接口客户端：betard 房间信息 + getH5Play 拉流地址（含 ub98484234 签名）。
/// </summary>
public sealed class DouyuApiClient : IDisposable
{
    private const string BetardEndpoint = "https://www.douyu.com/betard/{0}";
    private const string GetH5PlayEndpoint = "https://www.douyu.com/lapi/live/getH5Play/{0}";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>> _signProvider;
    private readonly DouyuSigner? _ownedSigner;

    public DouyuApiClient()
        : this(null, null)
    {
    }

    public DouyuApiClient(HttpClient? httpClient)
        : this(httpClient, null)
    {
    }

    internal DouyuApiClient(
        HttpClient? httpClient,
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? signProvider)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        if (signProvider is null)
        {
            var signer = new DouyuSigner(_httpClient);
            _ownedSigner = signer;
            _signProvider = signer.SignAsync;
        }
        else
        {
            _signProvider = signProvider;
        }
    }

    /// <summary>查询房间状态；开播时附带给出一路可拉流地址。</summary>
    public Task<DouyuRoomSnapshot> GetRoomAsync(string roomId, CancellationToken ct = default)
        => GetRoomAsync(roomId, cookie: null, ct);

    /// <summary>
    /// 查询房间状态；<paramref name="cookie"/> 为该平台登录 Cookie。
    /// 斗鱼匿名也能拿到可播地址，但登录态才能在 getH5Play 里协商到蓝光档位，因此统一透传。
    /// </summary>
    public async Task<DouyuRoomSnapshot> GetRoomAsync(string roomId, string? cookie, CancellationToken ct = default)
    {
        if (!DouyuUrlParser.IsRoomNumber(roomId))
        {
            throw new ArgumentException($"斗鱼房间号必须为数字：{roomId}", nameof(roomId));
        }

        var safeCookie = NormalizeCookie(cookie);
        var betardUrl = string.Format(BetardEndpoint, Uri.EscapeDataString(roomId));
        using var betard = await GetJsonAsync(betardUrl, safeCookie, ct);
        if (!betard.RootElement.TryGetProperty("room", out var room) || room.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"斗鱼房间信息格式异常（缺少 room 对象）：{roomId}");
        }

        var roomName = room.TryGetProperty("room_name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var nick = room.TryGetProperty("nickname", out var nickElement) ? nickElement.GetString() ?? string.Empty : string.Empty;
        long ownerUid = 0;
        if (room.TryGetProperty("owner_uid", out var uidElement))
        {
            if (uidElement.ValueKind == JsonValueKind.Number && uidElement.TryGetInt64(out var uidNumber))
            {
                ownerUid = uidNumber;
            }
            else if (uidElement.ValueKind == JsonValueKind.String && long.TryParse(uidElement.GetString(), out var uidText))
            {
                ownerUid = uidText;
            }
        }

        var liveStatus = room.TryGetProperty("show_status", out var statusElement) && statusElement.TryGetInt32(out var status)
            ? status
            : 0;
        var normalizedRoomId = room.TryGetProperty("room_id", out var roomIdElement)
            ? (roomIdElement.ValueKind == JsonValueKind.Number ? roomIdElement.GetInt64().ToString() : roomIdElement.GetString() ?? roomId)
            : roomId;

        if (liveStatus != 1)
        {
            return new DouyuRoomSnapshot(normalizedRoomId, roomName, nick, ownerUid, false, Array.Empty<StreamQuality>());
        }

        var quality = await GetStreamQualityAsync(normalizedRoomId, safeCookie, ct);
        var qualities = quality is null ? Array.Empty<StreamQuality>() : new[] { quality };
        return new DouyuRoomSnapshot(normalizedRoomId, roomName, nick, ownerUid, true, qualities);
    }

    private async Task<StreamQuality?> GetStreamQualityAsync(string roomId, string? cookie, CancellationToken ct)
    {
        var signature = await _signProvider(roomId, ct);
        var form = new Dictionary<string, string>(signature, StringComparer.Ordinal)
        {
            ["rate"] = "-1",
        };

        var url = string.Format(GetH5PlayEndpoint, Uri.EscapeDataString(roomId));
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await ReadBodyAsync(response, ct);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var errorCode = root.TryGetProperty("error", out var errorElement) ? errorElement.GetInt32() : 0;
        if (errorCode != 0)
        {
            var message = root.TryGetProperty("msg", out var msgElement) ? msgElement.GetString() : null;
            throw new InvalidOperationException($"斗鱼获取播放地址失败（error={errorCode}）：{message}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rtmpUrl = data.TryGetProperty("rtmp_url", out var rtmpUrlElement) ? rtmpUrlElement.GetString() : null;
        var rtmpLive = data.TryGetProperty("rtmp_live", out var rtmpLiveElement) ? rtmpLiveElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(rtmpUrl) || string.IsNullOrWhiteSpace(rtmpLive))
        {
            return null;
        }

        var streamUrl = BuildStreamUrl(rtmpUrl!, rtmpLive!);
        var rate = data.TryGetProperty("rate", out var rateElement) && rateElement.TryGetInt32(out var rateValue) ? rateValue : 0;
        var displayName = ResolveQualityName(data, rate);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = "https://www.douyu.com/" + roomId,
        };
        if (cookie is not null)
        {
            headers["Cookie"] = cookie;
        }

        return new StreamQuality(
            Id: "douyu-" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayName: displayName,
            Height: 0,
            Fps: 0,
            StreamUrl: streamUrl,
            RequiredHeaders: headers);
    }

    private static string BuildStreamUrl(string baseUrl, string livePath)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        if (livePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            livePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return livePath;
        }

        return trimmedBase + "/" + livePath.TrimStart('/');
    }

    private static string ResolveQualityName(JsonElement data, int selectedRate)
    {
        if (data.TryGetProperty("multirates", out var rates) && rates.ValueKind == JsonValueKind.Array)
        {
            foreach (var rateElement in rates.EnumerateArray())
            {
                if (rateElement.TryGetProperty("rate", out var rateValue) && rateValue.TryGetInt32(out var rate) && rate == selectedRate)
                {
                    var name = rateElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name!;
                    }
                }
            }
        }

        return "原画";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await ReadBodyAsync(response, ct);
        return JsonDocument.Parse(body);
    }

    /// <summary>清洗 Cookie：去掉换行，空值返回 null（防止请求头注入）。</summary>
    internal static string? NormalizeCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var builder = new StringBuilder(cookie.Trim().Length);
        foreach (var ch in cookie.Trim())
        {
            builder.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var encoding = response.Content.Headers.ContentEncoding.ToString();
        Stream? decompressed = null;
        try
        {
            if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                decompressed = new GZipStream(stream, CompressionMode.Decompress);
            }
            else if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
            {
                decompressed = new DeflateStream(stream, CompressionMode.Decompress);
            }
            else if (encoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                decompressed = new BrotliStream(stream, CompressionMode.Decompress);
            }

            var target = decompressed ?? stream;
            using var reader = new StreamReader(target, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        finally
        {
            if (decompressed is not null)
            {
                await decompressed.DisposeAsync();
            }
        }
    }

    public void Dispose()
    {
        _ownedSigner?.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
