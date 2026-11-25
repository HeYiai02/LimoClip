using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 容错读取录制伴随弹幕 .jsonl：
/// 首行是 <c>{"type":"meta",...}</c>，之后每行是 <c>{"type":"dm","offsetMs":...,"uname":...,"text":...}</c>。
/// 进程崩溃或磁盘写满时文件尾可能出现半行 JSON，读取时逐行解析并跳过坏行，不因单行异常丢掉整份弹幕。
/// </summary>
public static class DanmakuJsonlReader
{
    private const int BufferSize = 64 * 1024;

    /// <summary>单次增量读取最多消费的字节数，避免一次同步把长时间录制的弹幕全部拉进内存。</summary>
    public const int MaxIncrementalReadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 读取整份 .jsonl 为按 <see cref="RecordedDanmakuItem.OffsetMs"/> 升序排列的列表。
    /// </summary>
    /// <exception cref="FileNotFoundException">弹幕文件不存在。</exception>
    public static async Task<IReadOnlyList<RecordedDanmakuItem>> ReadAllAsync(
        string jsonlPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonlPath);
        if (!File.Exists(jsonlPath))
        {
            throw new FileNotFoundException("找不到伴随弹幕文件。", jsonlPath);
        }

        var items = new List<RecordedDanmakuItem>(capacity: 1024);

        await using var stream = new FileStream(
            jsonlPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (TryParseLine(line, out var item))
            {
                items.Add(item);
            }
        }

        if (!IsSorted(items))
        {
            items.Sort(static (left, right) => left.OffsetMs.CompareTo(right.OffsetMs));
        }

        return items;
    }

    /// <summary>
    /// 从 <paramref name="fromByteOffset"/> 起增量读取新增的弹幕行，供「边录边剪」跟随仍在写入的
    /// .jsonl 文件：只返回完整行（文件尾部尚未写完换行的半截行不消费，偏移不推进），
    /// 单次最多消费 <see cref="MaxIncrementalReadBytes"/> 字节。
    /// <para>
    /// 读取端用 <see cref="FileShare.ReadWrite"/> 打开，允许录制器同时持有写句柄；
    /// 若文件被截断/重建（长度小于偏移）会从头重读，避免永久停在无效偏移。
    /// </para>
    /// </summary>
    /// <param name="jsonlPath">伴随弹幕 .jsonl 路径。</param>
    /// <param name="fromByteOffset">上次读取返回的 <see cref="IncrementalDanmakuReadResult.NextByteOffset"/>；负数按 0 处理。</param>
    /// <param name="ct">取消标记。</param>
    /// <exception cref="FileNotFoundException">弹幕文件不存在。</exception>
    public static async Task<IncrementalDanmakuReadResult> ReadIncrementalAsync(
        string jsonlPath,
        long fromByteOffset,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonlPath);
        if (!File.Exists(jsonlPath))
        {
            throw new FileNotFoundException("找不到伴随弹幕文件。", jsonlPath);
        }

        var items = new List<RecordedDanmakuItem>(capacity: 64);

        await using var stream = new FileStream(
            jsonlPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var start = Math.Max(0, fromByteOffset);
        if (start > stream.Length)
        {
            // 文件被截断或重建（例如同一路径开始新一场录制），从头重读。
            start = 0;
        }

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        var pending = new List<byte>(capacity: 256);
        var remaining = MaxIncrementalReadBytes;
        var consumed = start;
        var readOffset = start;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var read = await stream
                .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            remaining -= read;
            var chunkStart = readOffset;
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value != (byte)'\n')
                {
                    pending.Add(value);
                    continue;
                }

                TryAddLine(pending, items);
                pending.Clear();
                consumed = chunkStart + i + 1;
            }

            readOffset = chunkStart + read;
        }

        return new IncrementalDanmakuReadResult(items, consumed);
    }

    /// <summary>解析单行；meta 行、未知类型、坏 JSON 与缺字段行返回 false。</summary>
    public static bool TryParseLine(string? line, out RecordedDanmakuItem item)
    {
        item = default!;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("type", out var typeElement))
            {
                if (typeElement.ValueKind != JsonValueKind.String
                    || !string.Equals(typeElement.GetString(), "dm", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!root.TryGetProperty("offsetMs", out var offsetElement)
                || offsetElement.ValueKind != JsonValueKind.Number
                || !offsetElement.TryGetInt64(out var offsetMs))
            {
                return false;
            }

            if (offsetMs < 0)
            {
                offsetMs = 0;
            }

            var senderName = root.TryGetProperty("uname", out var unameElement) && unameElement.ValueKind == JsonValueKind.String
                ? unameElement.GetString() ?? string.Empty
                : string.Empty;

            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;

            var isSticker = root.TryGetProperty("isSticker", out var stickerElement)
                && stickerElement.ValueKind is JsonValueKind.True;

            var isGift = root.TryGetProperty("isGift", out var giftElement)
                && giftElement.ValueKind is JsonValueKind.True;

            item = new RecordedDanmakuItem(offsetMs, senderName, text, isSticker, isGift);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把一行原始字节解码为字符串并交给 <see cref="TryParseLine"/>：
    /// 去掉行尾 <c>\r</c> 与（首行的）UTF-8 BOM，空行跳过。
    /// </summary>
    private static void TryAddLine(List<byte> bytes, List<RecordedDanmakuItem> items)
    {
        var count = bytes.Count;
        if (count > 0 && bytes[count - 1] == (byte)'\r')
        {
            count--;
        }

        var start = 0;
        if (count >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            start = 3;
        }

        if (count <= start)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(bytes.ToArray(), start, count - start);
        if (TryParseLine(line, out var item))
        {
            items.Add(item);
        }
    }

    private static bool IsSorted(List<RecordedDanmakuItem> items)
    {
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i - 1].OffsetMs > items[i].OffsetMs)
            {
                return false;
            }
        }

        return true;
    }
}
