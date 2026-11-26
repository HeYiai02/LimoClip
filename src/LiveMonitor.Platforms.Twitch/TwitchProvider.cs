using System.Net;
using System.Text;
using System.Text.Json;
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
        if (TwitchProviderParsing.IsChannelName(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!TwitchProviderParsing.IsTwitchHost(uri.Host) || !TwitchProviderParsing.TryGetChannelSegment(uri, out var channel))
        {
            return false;
        }

        return TwitchProviderParsing.IsChannelName(channel);
    }

    public string ExtractRoomId(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("房间链接不能为空。", nameof(roomUrl));
        }

        var value = roomUrl.Trim();
        if (TwitchProviderParsing.IsChannelName(value))
        {
            return value.ToLowerInvariant();
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"无法识别的 Twitch 频道链接：{roomUrl}", nameof(roomUrl));
        }

        if (!TwitchProviderParsing.IsTwitchHost(uri.Host) || !TwitchProviderParsing.TryGetChannelSegment(uri, out var channel) || !TwitchProviderParsing.IsChannelName(channel))
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
            if (TwitchProviderParsing.IsOffline(stderr))
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
            return TwitchProviderParsing.ParseStatus(channel, document.RootElement);
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
            var qualities = TwitchProviderParsing.ParseMasterM3u8(playlist, channel);
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
            var authToken = TwitchProviderParsing.ExtractAuthToken(normalizedCookie);
            var token = await RequestPlaybackAccessTokenAsync(channel, authToken, ct).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            var usherUrl = TwitchProviderParsing.BuildUsherUrl(channel, token.Value.Token, token.Value.Signature);
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
            var qualities = TwitchProviderParsing.ParseMasterM3u8(playlist, channel);
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
        // 实测（2026-11-24，约 130 秒连续采样）：site / popout / mobile_web 的 media playlist 会拼接
        // twitch-stitched-ad（30 秒广告），而 embed 通道始终只返回直播源（X-TV-TWITCH-STREAM-SOURCE="live"），
        // 因此这里固定用 embed，避免直播中插入 SSAI 广告。
        const string playerType = "embed";
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
            AddGqlHeaders(request, TwitchProviderParsing.ExtractAuthToken(cookie));
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

}
