using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// <see cref="StreamQuality"/> → mpv 流选项的纯映射：demuxer 缓存档位、UA、Referer、
/// Cookie 头与分离音频地址，以及请求头大小写查找和 CR/LF 清理。
/// 与 mpv、Avalonia、文件系统无关，便于单测；真正写入 mpv 的部分仍在 <see cref="MpvPlayerView"/>。
/// </summary>
internal static class StreamOptionMapper
{
    /// <summary>未显式给出 UA 时使用的桌面浏览器 UA（与 1.0 版保持一致）。</summary>
    internal const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>720P 及以上档位的 demuxer 缓存上限（HLS 分段/网络抖动时给音频更多缓冲）。</summary>
    internal const string HighResolutionDemuxerMaxBytes = "8M";

    /// <summary>低于 720P 档位的 demuxer 缓存上限（保持低内存）。</summary>
    internal const string LowResolutionDemuxerMaxBytes = "3M";

    /// <summary>mpv 需要的一组流选项；值为 null 表示不设置该选项（Cookie 同时刻只拼一个 http-header-fields）。</summary>
    internal sealed record PlayerStreamOptions(
        string DemuxerMaxBytes,
        string UserAgent,
        string? Referrer,
        string? HttpHeaderFields,
        string? AudioFile);

    /// <summary>按分辨率选择 demuxer 缓存、补齐默认 UA、清理可选头并把 Cookie 拼成 mpv 的 http-header-fields。</summary>
    internal static PlayerStreamOptions Build(StreamQuality quality)
    {
        var demuxerMaxBytes = quality.Height >= 720
            ? HighResolutionDemuxerMaxBytes
            : LowResolutionDemuxerMaxBytes;

        var userAgent = FindHeader(quality, "User-Agent") ?? DefaultUserAgent;
        var referer = FindHeader(quality, "Referer");
        var cookie = FindHeader(quality, "Cookie");

        return new PlayerStreamOptions(
            demuxerMaxBytes,
            SanitizeHeaderValue(userAgent),
            string.IsNullOrWhiteSpace(referer) ? null : SanitizeHeaderValue(referer),
            string.IsNullOrWhiteSpace(cookie) ? null : "Cookie: " + SanitizeHeaderValue(cookie),
            quality.HasSeparateAudio ? quality.AudioStreamUrl?.Trim() : null);
    }

    /// <summary>在 <see cref="StreamQuality.RequiredHeaders"/> 中大小写不敏感地查找请求头；缺失返回 null。</summary>
    internal static string? FindHeader(StreamQuality quality, string name)
    {
        foreach (var (key, value) in quality.RequiredHeaders)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>清理请求头值：去掉 CR、LF 换成空格，防止 header 注入并保持 mpv 单行语义。</summary>
    internal static string SanitizeHeaderValue(string value)
        => value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}
