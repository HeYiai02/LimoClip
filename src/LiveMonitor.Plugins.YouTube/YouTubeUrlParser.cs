using System.Text.RegularExpressions;

namespace LiveMonitor.Plugins.YouTube;

/// <summary>
/// YouTube 链接与房间标识的解析/规范化。
/// <para>
/// 房间标识统一存三种形式：视频 ID（11 位）、频道句柄（<c>@handle</c>）、频道 ID（<c>channel:UC…</c>）。
/// 视频 ID 可直接播放；句柄与频道 ID 需要 yt-dlp 解析当前直播的视频 ID。
/// </para>
/// </summary>
public static class YouTubeUrlParser
{
    /// <summary>YouTube 视频 ID 形如 <c>dQw4w9WgXcQ</c>。</summary>
    public const string VideoIdPattern = "^[A-Za-z0-9_-]{11}$";

    private const string ChannelIdPrefix = "channel:";

    private static readonly Regex VideoIdRegex = new(
        VideoIdPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HandleRegex = new(
        "^@[A-Za-z0-9_.-]{1,60}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelIdRegex = new(
        "^channel:UC[A-Za-z0-9_-]{20,30}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>判断输入是否像 YouTube 链接或可识别的房间标识。</summary>
    public static bool CanHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (IsVideoId(trimmed) || IsHandle(trimmed) || IsChannelId(trimmed))
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsYouTubeHost(uri.Host))
        {
            return false;
        }

        return TryExtractFromUri(uri, out _);
    }

    /// <summary>提取并规范化房间标识；无法识别时抛 <see cref="ArgumentException"/>。</summary>
    public static string ExtractRoomId(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("房间链接不能为空。", nameof(roomUrl));
        }

        var trimmed = roomUrl.Trim();
        if (IsVideoId(trimmed))
        {
            return trimmed;
        }

        if (IsHandle(trimmed))
        {
            return trimmed;
        }

        if (IsChannelId(trimmed))
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"无法识别的 YouTube 链接：{roomUrl}", nameof(roomUrl));
        }

        if (!IsYouTubeHost(uri.Host) || !TryExtractFromUri(uri, out var roomId))
        {
            throw new ArgumentException($"无法从链接中识别 YouTube 房间：{roomUrl}", nameof(roomUrl));
        }

        return roomId;
    }

    /// <summary>把房间标识转换为 yt-dlp 可解析的直播页地址。</summary>
    public static string BuildLiveUrl(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("YouTube 房间标识不能为空。", nameof(roomId));
        }

        var value = roomId.Trim();
        if (IsVideoId(value))
        {
            return $"https://www.youtube.com/watch?v={value}";
        }

        if (IsHandle(value))
        {
            return $"https://www.youtube.com/{value}/live";
        }

        if (IsChannelId(value))
        {
            return $"https://www.youtube.com/channel/{value[ChannelIdPrefix.Length..]}/live";
        }

        throw new ArgumentException($"无法识别的 YouTube 房间标识：{roomId}", nameof(roomId));
    }

    /// <summary>是否 11 位视频 ID。</summary>
    public static bool IsVideoId(string? value)
        => !string.IsNullOrWhiteSpace(value) && VideoIdRegex.IsMatch(value.Trim());

    /// <summary>是否 <c>@handle</c> 形式。</summary>
    public static bool IsHandle(string? value)
        => !string.IsNullOrWhiteSpace(value) && HandleRegex.IsMatch(value.Trim());

    /// <summary>是否 <c>channel:UC…</c> 形式。</summary>
    public static bool IsChannelId(string? value)
        => !string.IsNullOrWhiteSpace(value) && ChannelIdRegex.IsMatch(value.Trim());

    private static bool IsYouTubeHost(string host)
        => host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.youtu.be", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractFromUri(Uri uri, out string roomId)
    {
        roomId = string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            // https://youtu.be/{id}
            if (segments.Length >= 1 && IsVideoId(segments[0]))
            {
                roomId = segments[0];
                return true;
            }

            return false;
        }

        if (segments.Length == 0)
        {
            return false;
        }

        var first = Uri.UnescapeDataString(segments[0]);

        if (first.Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = GetQueryValue(uri, "v");
            if (!string.IsNullOrWhiteSpace(videoId) && IsVideoId(videoId))
            {
                roomId = videoId;
                return true;
            }

            return false;
        }

        if (first.Equals("live", StringComparison.OrdinalIgnoreCase)
            || first.Equals("shorts", StringComparison.OrdinalIgnoreCase)
            || first.Equals("embed", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length >= 2 && IsVideoId(segments[1]))
            {
                roomId = segments[1];
                return true;
            }

            return false;
        }

        if (first.StartsWith('@'))
        {
            // https://www.youtube.com/@handle、/@handle/live、/@handle/streams
            if (IsHandle(first))
            {
                roomId = first;
                return true;
            }

            return false;
        }

        if (first.Equals("channel", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length >= 2
                && segments[1].StartsWith("UC", StringComparison.Ordinal)
                && segments[1].Length is >= 22 and <= 32)
            {
                roomId = ChannelIdPrefix + segments[1];
                return true;
            }

            return false;
        }

        return false;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return null;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            if (!pair[..index].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(index + 1)..]);
        }

        return null;
    }
}
