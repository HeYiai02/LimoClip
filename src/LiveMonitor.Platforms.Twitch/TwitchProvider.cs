using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch 通过 yt-dlp -j 获取直播元数据与 HLS 地址。
/// </summary>
public sealed class TwitchProvider : IPlatformProvider
{
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

    public TwitchProvider(string ytDlpPath)
        : this(new CliWrapYtDlpRunner(ytDlpPath))
    {
    }

    public TwitchProvider(IYtDlpRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
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
