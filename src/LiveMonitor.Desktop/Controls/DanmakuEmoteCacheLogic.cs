using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕表情图片缓存的纯规则：URL 归一化、磁盘缓存键、失败重试冷却、软上限与载荷有效性判定。
/// 不依赖 Avalonia/网络/文件系统，便于直接单测。
/// </summary>
internal static class DanmakuEmoteCacheLogic
{
    /// <summary>内存 LRU 最多缓存的已解码图片数量。</summary>
    internal const int MaxMemoryEntries = 200;

    /// <summary>磁盘缓存软上限（超限后本次不再写入，避免无限膨胀）。</summary>
    internal const int DiskCacheMaxFiles = 2_000;

    /// <summary>失败后允许重试的冷却时间：网络抖动/临时 403 不应让某个表情整场都只显示文本。</summary>
    internal const long FailedRetryMilliseconds = 60_000;

    /// <summary>磁盘缓存文件扩展名。</summary>
    internal const string DiskFileExtension = ".img";

    /// <summary>
    /// 平台下发的表情地址可能是协议相对 URL（<c>//i0.hdslb.com/...</c>，B 站常见）或带空白。
    /// 统一补全为 https 绝对地址，否则 HttpClient 会直接抛异常、整场表情都退回文本。
    /// </summary>
    internal static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }

        return trimmed;
    }

    /// <summary>磁盘缓存文件名：URL 的 SHA1 十六进制小写 + <see cref="DiskFileExtension"/>。</summary>
    internal static string DiskFileName(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))).ToLowerInvariant();
        return hash + DiskFileExtension;
    }

    /// <summary>失败冷却结束后允许重试（冷却按毫秒单调时钟计）。</summary>
    internal static bool ShouldRetryAfterFailure(long failedAtMilliseconds, long nowMilliseconds)
    {
        var elapsed = nowMilliseconds - failedAtMilliseconds;
        return elapsed >= FailedRetryMilliseconds;
    }

    /// <summary>载荷可用性：空/零字节视为下载或解码失败。</summary>
    internal static bool HasUsablePayload([NotNullWhen(true)] byte[]? payload)
        => payload is { Length: > 0 };

    /// <summary>磁盘缓存是否仍可写入（未超过软上限）。</summary>
    internal static bool CanWriteDisk(int existingFileCount)
        => existingFileCount <= DiskCacheMaxFiles;
}
