using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 表情图片缓存：内存 LRU（最多 200 张已解码 Bitmap）+ 本地磁盘缓存。
/// 只渲染静态帧：GIF/动态 WebP 由 Avalonia 解码首帧，绝不在渲染层跑动画。
/// 下载/解码全部在后台线程完成，UI 线程只在缓存命中时取图。
/// </summary>
internal static class DanmakuEmoteImageCache
{
    private const int MaxMemoryEntries = 200;
    private const int DiskCacheMaxFiles = 2_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<(string Url, Bitmap Bitmap)>> Entries = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Url, Bitmap Bitmap)> Lru = new();
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = CreateHttpClient();
    private static string? _diskDirectory;

    /// <summary>命中内存缓存时返回 true；未命中时由调用方退回文本渲染。</summary>
    internal static bool TryGet(string url, out Bitmap? bitmap)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(url, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }

        bitmap = null;
        return false;
    }

    /// <summary>发起后台加载；同一 URL 只会加载一次，失败后本进程内不再重试。</summary>
    internal static void Request(string url)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Request(url));
            return;
        }

        lock (Gate)
        {
            if (Entries.ContainsKey(url) || Loading.Contains(url) || Failed.Contains(url))
            {
                return;
            }

            Loading.Add(url);
        }

        _ = LoadAsync(url);
    }

    internal static void ClearMemory()
    {
        lock (Gate)
        {
            foreach (var node in Lru)
            {
                node.Bitmap.Dispose();
            }

            Lru.Clear();
            Entries.Clear();
        }
    }

    private static async Task LoadAsync(string url)
    {
        byte[]? bytes = null;
        try
        {
            var path = DiskPath(url);
            if (path is not null && File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
        }
        catch
        {
            bytes = null;
        }

        if (bytes is null || bytes.Length == 0)
        {
            try
            {
                bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            catch
            {
                bytes = null;
            }
        }

        var payload = bytes;
        await Dispatcher.UIThread.InvokeAsync(() => Complete(url, payload));
    }

    private static void Complete(string url, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            MarkFailed(url);
            return;
        }

        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
        }
        catch
        {
            MarkFailed(url);
            return;
        }

        lock (Gate)
        {
            Loading.Remove(url);
            if (Entries.TryGetValue(url, out var existing))
            {
                Lru.Remove(existing);
                existing.Value.Bitmap.Dispose();
            }

            var node = Lru.AddFirst((url, bitmap));
            Entries[url] = node;
            while (Lru.Count > MaxMemoryEntries)
            {
                var last = Lru.Last;
                if (last is null)
                {
                    break;
                }

                Lru.RemoveLast();
                Entries.Remove(last.Value.Url);
                last.Value.Bitmap.Dispose();
            }
        }

        _ = Task.Run(() => TryWriteDisk(url, bytes));
    }

    private static void MarkFailed(string url)
    {
        lock (Gate)
        {
            Loading.Remove(url);
            Failed.Add(url);
        }
    }

    private static string? DiskPath(string url)
    {
        var directory = DiskDirectory();
        if (directory is null)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(directory, hash + ".img");
    }

    private static string? DiskDirectory()
    {
        if (_diskDirectory is not null)
        {
            return _diskDirectory;
        }

        foreach (var candidate in EnumerateDiskCandidates())
        {
            try
            {
                Directory.CreateDirectory(candidate);
                _diskDirectory = candidate;
                return candidate;
            }
            catch
            {
                // 当前目录不可写时尝试下一个候选；磁盘缓存失败不影响渲染。
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDiskCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "LiveMonitor", "emote-cache");
        }

        yield return Path.Combine(Path.GetTempPath(), "LiveMonitor", "emote-cache");
    }

    private static void TryWriteDisk(string url, byte[] bytes)
    {
        try
        {
            var path = DiskPath(url);
            if (path is null || File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                var files = Directory.EnumerateFiles(directory, "*.img");
                var count = 0;
                foreach (var _ in files)
                {
                    count++;
                    if (count > DiskCacheMaxFiles)
                    {
                        // 磁盘缓存只做软上限：超限后本次不再写入，避免无限膨胀。
                        return;
                    }
                }
            }

            File.WriteAllBytes(path, bytes);
        }
        catch
        {
            // 磁盘缓存失败不影响渲染。
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        return client;
    }
}
