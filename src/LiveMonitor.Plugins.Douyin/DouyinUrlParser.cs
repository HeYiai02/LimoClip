using System.Text.RegularExpressions;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// 抖音直播间链接解析。抖音网页版直播间链接形如 https://live.douyin.com/{web_rid}，
/// 其中 web_rid 是数字串（进入 webcast/room/web/enter 接口时使用）。
/// </summary>
public static partial class DouyinUrlParser
{
    private static readonly string[] AllowedHosts =
    {
        "live.douyin.com",
        "www.douyin.com",
        "douyin.com",
        "m.douyin.com",
    };

    /// <summary>判断是否为可解析的数字 web_rid（抖音 web_rid 至少 6 位）。</summary>
    public static bool IsRoomNumber(string? value)
        => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^\d{6,20}$");

    /// <summary>
    /// 是否为本插件可处理的链接。注意：纯数字不接收——纯数字由 B 站 Provider 处理，
    /// 避免在「添加直播间」时被抖音抢先命中。
    /// </summary>
    public static bool CanHandle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var text = url.Trim();
        if (IsRoomNumber(text)) return false;

        var withScheme = EnsureScheme(text);
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        if (!AllowedHosts.Contains(host)) return false;

        return TryExtractFromUri(uri, out _);
    }

    /// <summary>从链接中提取 web_rid；无法解析时抛出 ArgumentException。</summary>
    public static string ExtractRoomId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("抖音直播间链接不能为空。", nameof(url));
        var text = url.Trim();
        if (IsRoomNumber(text)) return text;

        var withScheme = EnsureScheme(text);
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            throw new ArgumentException($"不支持的抖音直播间链接：{text}", nameof(url));
        var host = uri.Host.ToLowerInvariant();
        if (!AllowedHosts.Contains(host))
            throw new ArgumentException($"不支持的抖音直播间链接：{text}", nameof(url));

        if (TryExtractFromUri(uri, out var roomId)) return roomId;
        throw new ArgumentException($"无法从抖音链接中解析出直播间号：{text}", nameof(url));
    }

    public static bool TryExtractRoomId(string? url, out string roomId)
    {
        roomId = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            roomId = ExtractRoomId(url);
            return roomId.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>补全 https:// 前缀（用户常直接粘贴 live.douyin.com/xxx）。</summary>
    public static string EnsureScheme(string url)
    {
        var text = url.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return text;
        return "https://" + text.TrimStart('/');
    }

    public static string BuildLiveUrl(string roomId) => $"https://live.douyin.com/{roomId}";

    private static bool TryExtractFromUri(Uri uri, out string roomId)
    {
        roomId = string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (var segment in segments)
        {
            if (IsRoomNumber(segment))
            {
                roomId = segment;
                return true;
            }
        }

        return false;
    }
}
