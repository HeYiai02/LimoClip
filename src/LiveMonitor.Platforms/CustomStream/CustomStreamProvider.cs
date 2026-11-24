using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms.CustomStream;

/// <summary>
/// 清流房间解析记录（由宿主从 <see cref="IRoomRepository"/> 按 RoomId 查询提供）。
/// </summary>
public sealed record CustomStreamEntry(
    string RoomId,
    string RawUrl,
    string Alias,
    string? CustomHeadersJson);

/// <summary>
/// 内置「自定义直链 / 清流」平台适配器。
/// 不依赖外部 CLI，不破坏 <see cref="IPlatformProvider"/> 契约，直接交由 libmpv / FFmpeg 拉流。
/// 清流没有开播查询 API，默认返回 IsLive = true，断流/未推流由播放器与录制器的退避重试处理。
/// </summary>
public sealed class CustomStreamProvider : IPlatformProvider
{
    /// <summary>专属流媒体协议：不区分大小写直接命中。</summary>
    private static readonly HashSet<string> DirectSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "rtmp", "rtmps", "rtsp", "rtsps", "srt", "mms", "mmsh"
    };

    /// <summary>HTTP(S) 直链按常见流媒体后缀识别；无后缀 CDN 链接由添加窗口手动勾选兜底。</summary>
    private static readonly string[] DirectHttpExtensions =
    {
        ".m3u8", ".m3u", ".flv", ".ts", ".mpd"
    };

    private readonly IRoomRepository? _rooms;

    public CustomStreamProvider(IRoomRepository? rooms = null)
    {
        _rooms = rooms;
    }

    public PlatformType Platform => PlatformType.CustomStream;

    public bool CanHandle(string roomUrl) => LooksLikeStreamUrl(roomUrl);

    /// <summary>
    /// 判断输入是否像一条清流直链（供添加窗口自动勾选「作为自定义清流添加」）。
    /// 注意：应在 B 站 / Twitch 等平台 Provider 匹配失败后再使用，避免误抢普通网页链接。
    /// </summary>
    public static bool LooksLikeStreamUrl(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(roomUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (DirectSchemes.Contains(uri.Scheme))
        {
            return true;
        }

        if (!IsHttpScheme(uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        foreach (var extension in DirectHttpExtensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 手动勾选「作为自定义清流添加」时的地址校验：允许任意受支持的绝对协议，
    /// 不要求 URL 带流媒体后缀（覆盖无后缀的 CDN Token 直链）。
    /// </summary>
    public static bool IsSupportedDirectUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsHttpScheme(uri) || DirectSchemes.Contains(uri.Scheme);
    }

    /// <summary>
    /// 为每条新添加的清流生成稳定短 ID（<c>cs_xxxxxxxx</c>），确保后续更换带新 Token 的 URL 时
    /// 不会触发 SQLite UNIQUE(Platform, RoomId) 冲突，并保留原槽位与录制配置。
    /// </summary>
    public string ExtractRoomId(string roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl))
        {
            throw new ArgumentException("清流地址不能为空。", nameof(roomUrl));
        }

        return "cs_" + Guid.NewGuid().ToString("N")[..8];
    }

    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var trimmed = roomId?.Trim() ?? string.Empty;
        string streamUrl;
        string displayTitle;
        string? customHeadersJson;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var directUri)
            && !string.IsNullOrEmpty(directUri.Scheme))
        {
            // 直接传入完整 URL（添加流程、单元测试或临时预览场景）。
            streamUrl = trimmed;
            displayTitle = BuildDefaultTitle(directUri);
            customHeadersJson = null;
        }
        else
        {
            var entry = await ResolveEntryAsync(trimmed, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"未找到清流房间配置：{trimmed}");

            if (string.IsNullOrWhiteSpace(entry.RawUrl))
            {
                throw new InvalidOperationException($"清流房间「{entry.Alias}」尚未配置有效的拉流地址。");
            }

            streamUrl = entry.RawUrl.Trim();
            displayTitle = string.IsNullOrWhiteSpace(entry.Alias) ? "自定义清流" : entry.Alias;
            customHeadersJson = entry.CustomHeadersJson;
        }

        var headers = ParseHeadersJson(customHeadersJson);

        // 仅 HTTP/HTTPS 协议允许附加自定义请求头与 Cookie；RTMP/RTSP/SRT 传 HTTP 头会直接失败。
        if (!IsHttpStream(streamUrl))
        {
            headers.Clear();
        }
        else if (!string.IsNullOrWhiteSpace(cookie) && !headers.ContainsKey("Cookie"))
        {
            headers["Cookie"] = cookie.Trim();
        }

        var quality = new StreamQuality(
            Id: "direct",
            DisplayName: "清流原画",
            Height: 1080,
            Fps: 60,
            StreamUrl: streamUrl,
            RequiredHeaders: headers);

        var status = new RoomStatus(
            RoomId: roomId,
            Platform: PlatformType.CustomStream,
            Title: displayTitle,
            AnchorName: displayTitle,
            IsLive: true,
            AvailableQualities: new[] { quality });

        return status;
    }

    private async Task<CustomStreamEntry?> ResolveEntryAsync(string roomId, CancellationToken ct)
    {
        if (_rooms is null || string.IsNullOrWhiteSpace(roomId))
        {
            return null;
        }

        var rooms = await _rooms.GetAllAsync(ct).ConfigureAwait(false);
        var room = rooms.FirstOrDefault(candidate =>
            candidate.Platform == PlatformType.CustomStream
            && string.Equals(candidate.RoomId, roomId, StringComparison.Ordinal));

        return room is null
            ? null
            : new CustomStreamEntry(room.RoomId, room.RawUrl, room.Alias, room.CustomHeadersJson);
    }

    /// <summary>容错解析自定义请求头 JSON；畸形 JSON 降级为无请求头。</summary>
    public static Dictionary<string, string> ParseHeadersJson(string? headersJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return result;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (parsed is null)
            {
                return result;
            }

            foreach (var (key, value) in parsed)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    result[key.Trim()] = value.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // 忽略畸形 JSON，降级为无自定义请求头。
        }

        return result;
    }

    /// <summary>把 Referer / User-Agent 等非空请求头序列化为 JSON（供 UI 保存）。</summary>
    public static string? BuildHeadersJson(string? referer, string? userAgent)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            headers["Referer"] = referer.Trim();
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers["User-Agent"] = userAgent.Trim();
        }

        return headers.Count == 0 ? null : JsonSerializer.Serialize(headers);
    }

    public static bool IsHttpStream(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && IsHttpScheme(uri);
    }

    private static bool IsHttpScheme(Uri uri)
    {
        return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDefaultTitle(Uri uri)
    {
        return string.IsNullOrWhiteSpace(uri.Host) ? "自定义清流" : $"清流 ({uri.Host})";
    }
}
