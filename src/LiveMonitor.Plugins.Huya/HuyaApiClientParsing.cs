using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙移动端房间接口的纯响应解析（无网络）：房间状态/错误文案、FLV 线路、清晰度档位、
/// 房间页压缩内容解码与数字房间号提取。
/// </summary>
internal static class HuyaApiClientParsing
{
    private static readonly Regex ProfileRoomPattern =
        new(@"""profileRoom""\s*:\s*(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>读取接口 envelope 的 <c>status</c>；缺失、非数字或非整数回 0（非整数视作接口异常，交由调用方按错误处理）。</summary>
    internal static int ReadStatus(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>读取接口 envelope 的 <c>message</c>；缺失回空串。</summary>
    internal static string ReadMessage(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>从房间页 HTML 中提取 <c>"profileRoom":&lt;数字&gt;</c>；未命中回 null。</summary>
    internal static string? ExtractProfileRoom(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var match = ProfileRoomPattern.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>解析 <c>data.stream.flv.multiLine</c>；缺字段/类型不符回空集合，跳过非对象与缺 url 的项。</summary>
    internal static IReadOnlyList<HuyaStreamLine> ParseFlvLines(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("stream", out var stream)
            || stream.ValueKind != JsonValueKind.Object
            || !stream.TryGetProperty("flv", out var flv)
            || flv.ValueKind != JsonValueKind.Object
            || !flv.TryGetProperty("multiLine", out var multiLine)
            || multiLine.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HuyaStreamLine>();
        }

        var lines = new List<HuyaStreamLine>();
        foreach (var item in multiLine.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            lines.Add(new HuyaStreamLine(
                ReadString(item, "cdnType"),
                (int)ReadInt64(item, "lineIndex"),
                (int)ReadInt64(item, "webPriorityRate"),
                url));
        }

        return lines;
    }

    /// <summary>解析 <c>liveData.bitRateInfo</c>（内嵌 JSON 字符串）；非法/非数组回空集合，跳过非对象与缺名字的项。</summary>
    internal static IReadOnlyList<HuyaBitRateInfo> ParseBitRates(JsonElement liveData)
    {
        var raw = ReadString(liveData, "bitRateInfo");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<HuyaBitRateInfo>();
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<HuyaBitRateInfo>();
            }

            var result = new List<HuyaBitRateInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(item, "sDisplayName");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result.Add(new HuyaBitRateInfo(name, (int)ReadInt64(item, "iBitRate")));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<HuyaBitRateInfo>();
        }
    }

    /// <summary>按 <c>Content-Encoding</c>（或 gzip 魔数）解压房间页；空输入回空串，未声明压缩按 UTF-8 解码。</summary>
    internal static string DecodeContent(byte[] bytes, ICollection<string> contentEncodings)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = contentEncodings.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (encoding is "gzip" || bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (encoding == "deflate")
        {
            using var deflate = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (encoding == "br")
        {
            using var brotli = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>读取字符串属性；元素非对象、属性缺失或类型不符回空串。</summary>
    internal static string ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>读取整数属性；元素非对象、属性缺失、类型不符或非整数回 0。</summary>
    internal static long ReadInt64(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : 0;
}
