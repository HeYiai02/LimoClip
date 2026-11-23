using System.Globalization;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.YouTube;

/// <summary>
/// YouTube 通过 yt-dlp -j 获取直播元数据与播放地址。
/// <para>
/// YouTube 没有像 Twitch 那样的频道在线接口，yt-dlp 对未开播的直播页会直接报错，
/// 因此「是否开播」既看 JSON 的 <c>is_live</c>，也看 yt-dlp 的 stderr 关键词。
/// </para>
/// </summary>
public sealed class YouTubeProvider : IPlatformProvider
{
    private readonly IYtDlpRunner _runner;

    public YouTubeProvider(string ytDlpPath)
        : this(new CliWrapYtDlpRunner(ytDlpPath))
    {
    }

    public YouTubeProvider(IYtDlpRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <inheritdoc />
    public PlatformType Platform => PlatformType.YouTube;

    /// <inheritdoc />
    public bool CanHandle(string roomUrl) => YouTubeUrlParser.CanHandle(roomUrl);

    /// <inheritdoc />
    public string ExtractRoomId(string roomUrl) => YouTubeUrlParser.ExtractRoomId(roomUrl);

    /// <inheritdoc />
    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        var normalizedRoomId = YouTubeUrlParser.ExtractRoomId(roomId);
        var normalizedCookie = PlatformDefaults.NormalizeCookie(cookie);
        var url = YouTubeUrlParser.BuildLiveUrl(normalizedRoomId);

        YtDlpResult result;
        try
        {
            result = await _runner.RunJsonAsync(url, normalizedCookie, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"调用 yt-dlp 解析 YouTube 直播 {normalizedRoomId} 失败：{ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            var stderr = PlatformDefaults.TrimForError(result.StandardError);
            if (IsOffline(stderr))
            {
                return RoomStatus.Offline(normalizedRoomId, Platform);
            }

            throw new InvalidOperationException(
                $"yt-dlp 解析 YouTube 直播 {normalizedRoomId} 失败（exit {result.ExitCode}）：{stderr}");
        }

        var json = result.StandardOutput.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"yt-dlp 未返回 YouTube 直播 {normalizedRoomId} 的 JSON。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseStatus(normalizedRoomId, document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"yt-dlp 返回的 YouTube JSON 无法解析：{PlatformDefaults.TrimForError(json)}", ex);
        }
    }

    private static RoomStatus ParseStatus(string roomId, JsonElement root)
    {
        var title = JsonRead.String(root, "title") ?? string.Empty;
        var anchorName = JsonRead.String(root, "uploader")
            ?? JsonRead.String(root, "channel")
            ?? roomId;

        if (!JsonRead.Bool(root, "is_live"))
        {
            return RoomStatus.Offline(roomId, PlatformType.YouTube, title, anchorName);
        }

        var qualities = ParseFormats(root, roomId);
        if (qualities.Count == 0)
        {
            throw new InvalidOperationException($"yt-dlp 未返回 YouTube 直播 {roomId} 的可播放格式。");
        }

        return new RoomStatus(roomId, PlatformType.YouTube, title, anchorName, true, qualities);
    }

    private static IReadOnlyList<StreamQuality> ParseFormats(JsonElement root, string roomId)
    {
        var candidates = new Dictionary<(int Height, int Fps), FormatCandidate>();
        var audio = ParseAudioFormat(root, roomId);

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

            var quality = BuildQuality(format, roomId, height, fps, url);
            if (audio is { } audioTrack)
            {
                quality = quality with
                {
                    AudioStreamUrl = audioTrack.Url,
                    AudioHeaders = audioTrack.Headers,
                };
            }

            candidates[key] = new FormatCandidate(tbr, quality);
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Quality.Height)
            .ThenByDescending(candidate => candidate.Quality.Fps)
            .Select(candidate => candidate.Quality)
            .ToArray();
    }

    /// <summary>
    /// YouTube 直播 HLS 多数是视频/音频分离的 renditions；这里挑码率最高的纯音频轨，
    /// 供录音（ffmpeg 第二路输入）与播放（mpv audio-file）补齐声音。
    /// </summary>
    private static AudioTrackCandidate? ParseAudioFormat(JsonElement root, string roomId)
    {
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AudioTrackCandidate? best = null;
        var bestBitrate = -1d;

        foreach (var format in formats.EnumerateArray())
        {
            var url = JsonRead.String(format, "url");
            if (string.IsNullOrWhiteSpace(url)
                || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var videoCodec = JsonRead.String(format, "vcodec");
            var audioCodec = JsonRead.String(format, "acodec");
            var height = JsonRead.Int(format, "height");

            var noVideo = string.IsNullOrWhiteSpace(videoCodec)
                || videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (!noVideo || height > 0)
            {
                continue;
            }

            // 有明确音频编码的轨道优先；HLS 纯音频 rendition 有时不带 acodec，也接受。
            var hasAudioCodec = !string.IsNullOrWhiteSpace(audioCodec)
                && !audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            var bitrate = JsonRead.Double(format, "abr", JsonRead.Double(format, "tbr"));
            var rank = bitrate + (hasAudioCodec ? 0.5 : 0);
            if (rank <= bestBitrate)
            {
                continue;
            }

            bestBitrate = rank;
            best = new AudioTrackCandidate(url, BuildHeaders(format, roomId));
        }

        return best;
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(JsonElement format, string roomId)
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
            headers["Referer"] = YouTubeUrlParser.BuildLiveUrl(roomId);
        }

        return headers;
    }

    private static StreamQuality BuildQuality(
        JsonElement format,
        string roomId,
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
            headers["Referer"] = YouTubeUrlParser.BuildLiveUrl(roomId);
        }

        var displayName = $"{height}P" + (fps >= 50 ? fps.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return new StreamQuality(
            $"youtube-{height}p{fps.ToString(CultureInfo.InvariantCulture)}",
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
            || stderr.Contains("is not live", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is offline", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not live", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("live event will begin", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("premieres in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("has ended", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record FormatCandidate(double Tbr, StreamQuality Quality);

    /// <summary>纯音频 rendition：URL 与拉流所需的请求头。</summary>
    private sealed record AudioTrackCandidate(
        string Url,
        IReadOnlyDictionary<string, string> Headers);
}
