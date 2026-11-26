using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 拉流与元数据的纯解析规则（无状态、无网络）：频道链接识别、Cookie 令牌提取、Usher 地址拼接、
/// HLS 主播放列表与 yt-dlp JSON 的格式解析、离线错误判定。
/// <para>由 <see cref="TwitchProvider"/> 委托调用，便于直接单测。</para>
/// </summary>
internal static class TwitchProviderParsing
{
    internal const string UsherEndpointTemplate = "https://usher.ttvnw.net/api/channel/hls/{0}.m3u8";

    private static readonly HashSet<string> ReservedFirstSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "videos", "clip", "clips", "collections", "schedule", "directory",
        "search", "downloads", "settings", "subscriptions", "wallet",
        "drops", "turbo", "store", "p"
    };

    private static readonly Regex ChannelNamePattern = new(
        "^[A-Za-z0-9_]{1,25}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record FormatCandidate(double Tbr, StreamQuality Quality);

    /// <summary>从 Cookie 中提取 <c>auth-token</c>（不区分大小写，首个非空匹配）。</summary>
    internal static string? ExtractAuthToken(string? cookie)
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

    /// <summary>拼接 Usher HLS 主播放列表地址（附带固定参数与随机 nonce）。</summary>
    internal static string BuildUsherUrl(string channel, string token, string signature)
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
    internal static IReadOnlyList<StreamQuality> ParseMasterM3u8(string playlist, string channel)
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

    internal static Dictionary<string, string> ParseAttributes(string text)
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

    internal static (int Width, int Height) ParseResolution(string? resolution)
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

    /// <summary>把 yt-dlp 的频道 JSON 转为房间状态；离线返回 Offline，在线但无格式抛异常。</summary>
    internal static RoomStatus ParseStatus(string channel, JsonElement root)
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

    /// <summary>解析 yt-dlp formats：按 (height,fps) 去重，保留 TBR 最大的变体。</summary>
    internal static IReadOnlyList<StreamQuality> ParseFormats(JsonElement root, string channel)
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

    /// <summary>判定 yt-dlp 的标准错误是否表示频道离线。</summary>
    internal static bool IsOffline(string stderr)
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

    internal static bool IsTwitchHost(string host)
        => host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase)
            || host.Equals("go.twitch.tv", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetChannelSegment(Uri uri, out string channel)
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

    internal static bool IsChannelName(string value) => ChannelNamePattern.IsMatch(value);

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
}
