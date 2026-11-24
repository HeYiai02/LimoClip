using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 拉流与元数据（方案 B：去广告三层降级）。
/// <para>
/// 1) 外部去广告 M3U8 代理（<c>TwitchAdConfig.PlaylistProxyTemplateProvider</c> 提供 <c>{channel}</c> 模板）；
/// 2) 原生 GQL 获取 PlaybackAccessToken + Usher HLS 主播放列表（可带登录 Cookie，画质更全）；
/// 3) yt-dlp 兜底（保持旧实现与单参构造的行为，测试不联网）。
/// </para>
/// </summary>
public sealed class TwitchProvider : IPlatformProvider
{
    private const string GqlEndpoint = "https://gql.twitch.tv/gql";

    // 公开网页端 Client-ID（Twitch 在前端明文使用），用于匿名查询频道信息与播放令牌。
    private const string GqlClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    // PlaybackAccessToken 的 persisted query 哈希（网页播放器使用的固定操作）。
    private const string AccessTokenHash = "0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712";

    private const string UsherEndpointTemplate = "https://usher.ttvnw.net/api/channel/hls/{0}.m3u8";

    private static readonly HashSet<string> ReservedFirstSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "videos", "clip", "clips", "collections", "schedule", "directory",
        "search", "downloads", "settings", "subscriptions", "wallet",
        "drops", "turbo", "store", "p"
    };

    private static readonly Regex ChannelNamePattern = new(
        "^[A-Za-z0-9_]{1,25}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IYtDlpRunner _runner;
    private readonly HttpClient? _httpClient;
    private readonly Func<string?>? _playlistProxyTemplateProvider;

    /// <summary>yt-dlp 兜底模式（保持旧构造函数行为，不发起 HTTP 请求）。</summary>
    public TwitchProvider(string ytDlpPath)
        : this(new CliWrapYtDlpRunner(ytDlpPath))
    {
    }

    /// <summary>yt-dlp 兜底模式（注入 runner，供单元测试使用）。</summary>
    public TwitchProvider(IYtDlpRunner runner)
        : this(runner, httpClient: null, playlistProxyTemplateProvider: null)
    {
    }

    public TwitchProvider(
        string ytDlpPath,
        HttpClient? httpClient,
        Func<string?>? playlistProxyTemplateProvider = null)
        : this(new CliWrapYtDlpRunner(ytDlpPath), httpClient, playlistProxyTemplateProvider)
    {
    }

    /// <summary>
    /// 完整构造：<paramref name="httpClient"/> 为 null 时只走 yt-dlp 兜底（离线/测试场景）。
    /// </summary>
    public TwitchProvider(
        IYtDlpRunner runner,
        HttpClient? httpClient,
        Func<string?>? playlistProxyTemplateProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _httpClient = httpClient;
        _playlistProxyTemplateProvider = playlistProxyTemplateProvider;
    }

    public PlatformType Platform => PlatformType.Twitch;

    public bool CanHandle(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        var value = roomUrl.Trim();
        if (IsChannelName(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsTwitchHost(uri.Host) || !TryGetChannelSegment(uri, out var channel))
        {
            return false;
        }

        return IsChannelName(channel);
    }

    public string ExtractRoomId(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("房间链接不能为空。", nameof(roomUrl));
        }

        var value = roomUrl.Trim();
        if (IsChannelName(value))
        {
            return value.ToLowerInvariant();
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"无法识别的 Twitch 频道链接：{roomUrl}", nameof(roomUrl));
        }

        if (!IsTwitchHost(uri.Host) || !TryGetChannelSegment(uri, out var channel) || !IsChannelName(channel))
        {
            throw new ArgumentException($"无法从链接中识别 Twitch 频道：{roomUrl}", nameof(roomUrl));
        }

        return channel.ToLowerInvariant();
    }

    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        var channel = ExtractRoomId(roomId);
        var normalizedCookie = PlatformDefaults.NormalizeCookie(cookie);

        // 方案 B 第一、二层：代理模板 → 原生 GQL + Usher；任一成功即返回，失败静默降级。
        if (_httpClient is not null)
        {
            var proxyTemplate = _playlistProxyTemplateProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(proxyTemplate))
            {
                var proxied = await TryGetViaPlaylistProxyAsync(channel, proxyTemplate.Trim(), ct)
                    .ConfigureAwait(false);
                if (proxied is not null)
                {
                    return proxied;
                }
            }

            var native = await TryGetViaNativeApiAsync(channel, normalizedCookie, ct).ConfigureAwait(false);
            if (native is not null)
            {
                return native;
            }
        }

        return await GetViaYtDlpAsync(channel, normalizedCookie, ct).ConfigureAwait(false);
    }

    /// <summary>第三层：yt-dlp 兜底（旧实现，异常语义保持不变）。</summary>
    private async Task<RoomStatus> GetViaYtDlpAsync(
        string channel,
        string? normalizedCookie,
        CancellationToken ct)
    {
        YtDlpResult result;
        try
        {
            result = await _runner
                .RunJsonAsync($"{PlatformDefaults.TwitchBase}/{Uri.EscapeDataString(channel)}", normalizedCookie, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"调用 yt-dlp 解析 Twitch 频道 {channel} 失败：{ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            var stderr = PlatformDefaults.TrimForError(result.StandardError);
            if (IsOffline(stderr))
            {
                return RoomStatus.Offline(channel, Platform, anchorName: channel);
            }

            throw new InvalidOperationException(
                $"yt-dlp 解析 Twitch 频道 {channel} 失败（exit {result.ExitCode}）：{stderr}");
        }

        var json = result.StandardOutput.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"yt-dlp 未返回 Twitch 频道 {channel} 的 JSON。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseStatus(channel, document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"yt-dlp 返回的 Twitch JSON 无法解析：{PlatformDefaults.TrimForError(json)}", ex);
        }
    }

    /// <summary>第一层：外部去广告 M3U8 代理。失败（网络/404/非主播放列表）返回 null 继续降级。</summary>
    private async Task<RoomStatus?> TryGetViaPlaylistProxyAsync(
        string channel,
        string template,
        CancellationToken ct)
    {
        try
        {
            var url = template.Replace("{channel}", Uri.EscapeDataString(channel), StringComparison.OrdinalIgnoreCase);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", $"{PlatformDefaults.TwitchBase}/{channel}");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await _httpClient!
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var playlist = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var qualities = ParseMasterM3u8(playlist, channel);
            if (qualities.Count == 0)
            {
                return null;
            }

            var (title, anchorName) = await QueryChannelMetaAsync(channel, cookie: null, ct)
                .ConfigureAwait(false);
            return new RoomStatus(
                channel,
                Platform,
                title ?? string.Empty,
                string.IsNullOrWhiteSpace(anchorName) ? channel : anchorName!,
                true,
                qualities);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 代理不可用时静默回退到原生 API，不能因为外部服务挂掉影响观看。
            return null;
        }
    }

    /// <summary>第二层：GQL 获取播放令牌，再用 Usher 拉 HLS 主播放列表。</summary>
    private async Task<RoomStatus?> TryGetViaNativeApiAsync(
        string channel,
        string? normalizedCookie,
        CancellationToken ct)
    {
        try
        {
            var authToken = ExtractAuthToken(normalizedCookie);
            var token = await RequestPlaybackAccessTokenAsync(channel, authToken, ct).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            var usherUrl = BuildUsherUrl(channel, token.Value.Token, token.Value.Signature);
            using var request = new HttpRequestMessage(HttpMethod.Get, usherUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", $"{PlatformDefaults.TwitchBase}/{channel}");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await _httpClient!
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return RoomStatus.Offline(channel, Platform, anchorName: channel);
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var playlist = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var qualities = ParseMasterM3u8(playlist, channel);
            if (qualities.Count == 0)
            {
                return null;
            }

            var (title, anchorName) = await QueryChannelMetaAsync(channel, normalizedCookie, ct)
                .ConfigureAwait(false);
            return new RoomStatus(
                channel,
                Platform,
                title ?? string.Empty,
                string.IsNullOrWhiteSpace(anchorName) ? channel : anchorName!,
                true,
                qualities);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string Token, string Signature)?> RequestPlaybackAccessTokenAsync(
        string channel,
        string? authToken,
        CancellationToken ct)
    {
        // 带登录态时用 site（可拿订阅画质）；匿名时用 embed（服务端限制最少）。
        var playerType = string.IsNullOrWhiteSpace(authToken) ? "embed" : "site";
        var payload = new object[]
        {
            new
            {
                operationName = "PlaybackAccessToken",
                variables = new
                {
                    isLive = true,
                    login = channel,
                    isVod = false,
                    vodID = string.Empty,
                    playerType
                },
                extensions = new
                {
                    persistedQuery = new { version = 1, sha256Hash = AccessTokenHash }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, GqlEndpoint);
        AddGqlHeaders(request, authToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            if (!root[0].TryGetProperty("data", out var data)
                || !data.TryGetProperty("streamPlaybackAccessToken", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var value = JsonRead.String(accessToken, "value");
            var signature = JsonRead.String(accessToken, "signature");
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(signature))
            {
                return null;
            }

            return (value, signature);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>GQL 查询频道标题/主播名；失败返回 (null, null)，不阻塞播放。</summary>
    private async Task<(string? Title, string? AnchorName)> QueryChannelMetaAsync(
        string channel,
        string? cookie,
        CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                query = "query($login:String!){user(login:$login){displayName stream{title}}}",
                variables = new { login = channel }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, GqlEndpoint);
            AddGqlHeaders(request, ExtractAuthToken(cookie));
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var anchorName = JsonRead.String(user, "displayName");
            string? title = null;
            if (user.TryGetProperty("stream", out var stream)
                && stream.ValueKind == JsonValueKind.Object)
            {
                title = JsonRead.String(stream, "title");
            }

            return (title, anchorName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, null);
        }
    }

    private static void AddGqlHeaders(HttpRequestMessage request, string? authToken)
    {
        request.Headers.TryAddWithoutValidation("Client-Id", GqlClientId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", PlatformDefaults.UserAgent);
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {authToken}");
        }
    }

    private static string? ExtractAuthToken(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        foreach (var part in cookie.Split(';'))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var name = part[..index].Trim();
            if (!name.Equals("auth-token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(index + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string BuildUsherUrl(string channel, string token, string signature)
    {
        var nonce = Random.Shared.Next(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
        var query = string.Join(
            '&',
            "allow_source=true",
            "allow_audio_only=true",
            "fast_bread=true",
            "player_backend=mediaplayer",
            "playlist_include_framerate=true",
            "reassignments_supported=true",
            "supported_codecs=av1,h265,h264",
            $"p={nonce}",
            $"sig={Uri.EscapeDataString(signature)}",
            $"token={Uri.EscapeDataString(token)}");
        return string.Format(
            CultureInfo.InvariantCulture,
            UsherEndpointTemplate,
            Uri.EscapeDataString(channel)) + "?" + query;
    }

    /// <summary>解析 HLS 主播放列表：按 (height,fps) 去重，保留 BANDWIDTH 最大的变体。</summary>
    private static IReadOnlyList<StreamQuality> ParseMasterM3u8(string playlist, string channel)
    {
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return Array.Empty<StreamQuality>();
        }

        var candidates = new Dictionary<(int Height, int Fps), FormatCandidate>();
        var lines = playlist.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
            var (_, height) = ParseResolution(attributes.GetValueOrDefault("RESOLUTION"));
            if (height <= 0)
            {
                continue;
            }

            var fps = 30;
            if (attributes.TryGetValue("FRAME-RATE", out var frameRate)
                && double.TryParse(frameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
                && parsedFps > 0)
            {
                fps = (int)Math.Round(parsedFps, MidpointRounding.AwayFromZero);
            }

            var bandwidth = 0d;
            if (attributes.TryGetValue("BANDWIDTH", out var bandwidthText))
            {
                _ = double.TryParse(bandwidthText, NumberStyles.Float, CultureInfo.InvariantCulture, out bandwidth);
            }

            string? url = null;
            for (var next = index + 1; next < lines.Length; next++)
            {
                var candidateLine = lines[next].Trim();
                if (candidateLine.Length == 0 || candidateLine.StartsWith('#'))
                {
                    continue;
                }

                url = candidateLine;
                break;
            }

            if (string.IsNullOrWhiteSpace(url)
                || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = (height, fps);
            if (candidates.TryGetValue(key, out var current) && current.Tbr >= bandwidth)
            {
                continue;
            }

            candidates[key] = new FormatCandidate(
                bandwidth,
                CreateQuality(channel, height, fps, url));
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Quality.Height)
            .ThenByDescending(candidate => candidate.Quality.Fps)
            .Select(candidate => candidate.Quality)
            .ToArray();
    }

    private static StreamQuality CreateQuality(string channel, int height, int fps, string url)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PlatformDefaults.UserAgent,
            ["Referer"] = $"{PlatformDefaults.TwitchBase}/{channel}"
        };

        var displayName = $"{height}P" + (fps >= 50 ? fps.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return new StreamQuality(
            $"twitch-{height}p{fps.ToString(CultureInfo.InvariantCulture)}",
            displayName,
            height,
            fps,
            url,
            headers);
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < text.Length)
        {
            var equals = text.IndexOf('=', index);
            if (equals < 0)
            {
                break;
            }

            var key = text[index..equals].Trim().Trim(',');
            if (key.Length == 0)
            {
                break;
            }

            var valueStart = equals + 1;
            string value;
            if (valueStart < text.Length && text[valueStart] == '"')
            {
                var closing = text.IndexOf('"', valueStart + 1);
                if (closing < 0)
                {
                    value = text[(valueStart + 1)..];
                    index = text.Length;
                }
                else
                {
                    value = text[(valueStart + 1)..closing];
                    index = closing + 1;
                }
            }
            else
            {
                var comma = text.IndexOf(',', valueStart);
                if (comma < 0)
                {
                    value = text[valueStart..];
                    index = text.Length;
                }
                else
                {
                    value = text[valueStart..comma];
                    index = comma + 1;
                }
            }

            attributes[key] = value.Trim();
            while (index < text.Length && (text[index] == ',' || char.IsWhiteSpace(text[index])))
            {
                index++;
            }
        }

        return attributes;
    }

    private static (int Width, int Height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return (0, 0);
        }

        var parts = resolution.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return (0, 0);
        }

        return (width, height);
    }

    private static RoomStatus ParseStatus(string channel, JsonElement root)
    {
        var title = JsonRead.String(root, "title") ?? string.Empty;
        var anchorName = JsonRead.String(root, "uploader")
            ?? JsonRead.String(root, "channel")
            ?? channel;

        if (!JsonRead.Bool(root, "is_live"))
        {
            return RoomStatus.Offline(channel, PlatformType.Twitch, title, anchorName);
        }

        var qualities = ParseFormats(root, channel);
        if (qualities.Count == 0)
        {
            throw new InvalidOperationException($"yt-dlp 未返回 Twitch 频道 {channel} 的可播放格式。");
        }

        return new RoomStatus(channel, PlatformType.Twitch, title, anchorName, true, qualities);
    }

    private static IReadOnlyList<StreamQuality> ParseFormats(JsonElement root, string channel)
    {
        var candidates = new Dictionary<(int Height, int Fps), FormatCandidate>();

        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StreamQuality>();
        }

        foreach (var format in formats.EnumerateArray())
        {
            var url = JsonRead.String(format, "url");
            var videoCodec = JsonRead.String(format, "vcodec");
            var height = JsonRead.Int(format, "height");
            if (string.IsNullOrWhiteSpace(url)
                || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(videoCodec)
                || videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                || height <= 0)
            {
                continue;
            }

            var fps = (int)Math.Round(JsonRead.Double(format, "fps", 30), MidpointRounding.AwayFromZero);
            if (fps <= 0)
            {
                fps = 30;
            }

            var tbr = JsonRead.Double(format, "tbr");
            var key = (height, fps);
            if (candidates.TryGetValue(key, out var current) && current.Tbr >= tbr)
            {
                continue;
            }

            candidates[key] = new FormatCandidate(tbr, BuildQuality(format, channel, height, fps, url));
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Quality.Height)
            .ThenByDescending(candidate => candidate.Quality.Fps)
            .Select(candidate => candidate.Quality)
            .ToArray();
    }

    private static StreamQuality BuildQuality(
        JsonElement format,
        string channel,
        int height,
        int fps,
        string url)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("http_headers", out var httpHeaders)
            && httpHeaders.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in httpHeaders.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    headers[property.Name] = property.Value.GetString()!;
                }
            }
        }

        if (!headers.ContainsKey("User-Agent"))
        {
            headers["User-Agent"] = PlatformDefaults.UserAgent;
        }

        if (!headers.ContainsKey("Referer"))
        {
            headers["Referer"] = $"{PlatformDefaults.TwitchBase}/{channel}";
        }

        var displayName = $"{height}P" + (fps >= 50 ? fps.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return new StreamQuality(
            $"twitch-{height}p{fps.ToString(CultureInfo.InvariantCulture)}",
            displayName,
            height,
            fps,
            url,
            headers);
    }

    private static bool IsOffline(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        return stderr.Contains("not currently live", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is offline", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("channel is offline", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not live", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTwitchHost(string host)
        => host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("go.twitch.tv", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetChannelSegment(Uri uri, out string channel)
    {
        channel = string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || ReservedFirstSegments.Contains(segments[0]))
        {
            return false;
        }

        if (segments.Length > 1 && ReservedFirstSegments.Contains(segments[1]))
        {
            return false;
        }

        channel = Uri.UnescapeDataString(segments[0]);
        return true;
    }

    private static bool IsChannelName(string value) => ChannelNamePattern.IsMatch(value);

    private sealed record FormatCandidate(double Tbr, StreamQuality Quality);
}
