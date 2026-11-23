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
