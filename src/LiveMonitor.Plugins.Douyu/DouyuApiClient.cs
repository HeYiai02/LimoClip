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
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyuApiParsing.UserAgent);

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

        var safeCookie = DouyuApiParsing.NormalizeCookie(cookie);
        var betardUrl = string.Format(BetardEndpoint, Uri.EscapeDataString(roomId));
        using var betard = await GetJsonAsync(betardUrl, safeCookie, ct);
        var roomInfo = DouyuApiParsing.ParseRoom(betard.RootElement, roomId);

        if (!roomInfo.IsLive)
        {
            return new DouyuRoomSnapshot(
                roomInfo.RoomId,
                roomInfo.RoomName,
                roomInfo.Nick,
                roomInfo.OwnerUid,
                false,
                Array.Empty<StreamQuality>());
        }

        var quality = await GetStreamQualityAsync(roomInfo.RoomId, safeCookie, ct);
        var qualities = quality is null ? Array.Empty<StreamQuality>() : new[] { quality };
        return new DouyuRoomSnapshot(roomInfo.RoomId, roomInfo.RoomName, roomInfo.Nick, roomInfo.OwnerUid, true, qualities);
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
        return DouyuApiParsing.BuildQuality(document.RootElement, roomId, cookie);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DouyuApiParsing.UserAgent);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await ReadBodyAsync(response, ct);
        return JsonDocument.Parse(body);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var encoding = response.Content.Headers.ContentEncoding.ToString() ?? string.Empty;
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
