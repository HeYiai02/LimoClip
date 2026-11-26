using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Asr;

/// <summary>
/// OpenAI 兼容 ASR 响应解析与纯文本按句拆分（不依赖网络/FFmpeg，便于直接单测）：
/// verbose_json（segments 数组）优先使用时间戳；纯文本或单段响应按句末标点拆分，
/// 并按字符数比例估算每句时间；超长无标点文本按固定长度切块，保证长口播也能逐句跳转。
/// </summary>
internal static class AsrResponseParser
{
    /// <summary>
    /// 纯文本响应按句拆分时，单句超过该字符数后允许在逗号/顿号/冒号处再断一次，
    /// 避免一整段没有句号的口播变成一条超长行。
    /// </summary>
    internal const int MaxEstimatedSentenceChars = 48;

    /// <summary>没有句末标点的文本按该长度切块，保证长口播仍可点击跳转（约 2~3 秒一块）。</summary>
    internal const int FallbackChunkChars = 48;

    /// <summary>
    /// 解析 ASR 响应体：verbose_json 的 segments 数组按相对时间戳转成片段；否则把整段文本按句拆分。
    /// 空响应、非 JSON 对象、非法 JSON、无法识别文本时抛出带中文提示的 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal static AsrTranscriptResult Parse(
        string json,
        TimeSpan clipStart,
        TimeSpan clipEnd,
        string providerLabel,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("ASR API 返回了空响应。");
        }

        string fullText;
        var segments = new List<AsrTranscriptSegment>();
        var clipDuration = clipEnd - clipStart;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("ASR 响应不是 JSON 对象。");
            }

            fullText = root.TryGetProperty("text", out var textProperty)
                ? textProperty.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (root.TryGetProperty("segments", out var segmentArray) &&
                segmentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in segmentArray.EnumerateArray())
                {
                    double startSeconds = item.TryGetProperty("start", out var startProperty) &&
                                          startProperty.TryGetDouble(out var parsedStart)
                        ? parsedStart
                        : 0;
                    double endSeconds = item.TryGetProperty("end", out var endProperty) &&
                                        endProperty.TryGetDouble(out var parsedEnd)
                        ? parsedEnd
                        : startSeconds;
                    string text = item.TryGetProperty("text", out var segmentText)
                        ? segmentText.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var relativeStart = Clamp(TimeSpan.FromSeconds(startSeconds), TimeSpan.Zero, clipDuration);
                    var relativeEnd = Clamp(TimeSpan.FromSeconds(endSeconds), relativeStart, clipDuration);
                    segments.Add(new AsrTranscriptSegment(relativeStart, relativeEnd, text));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ASR 响应不是合法 JSON，无法解析。", ex);
        }

        if (segments.Count <= 1)
        {
            // 纯文本响应（硅基流动等只支持 json）或只返回一个整段：把文本按句末标点拆成多句，
            // 并按字符数比例估算每句的时间，让右侧列表可以逐句点击跳转。
            string text;
            TimeSpan rangeStart;
            TimeSpan rangeEnd;
            if (segments.Count == 1)
            {
                text = string.IsNullOrWhiteSpace(segments[0].Text) ? fullText : segments[0].Text;
                rangeStart = segments[0].RelativeStart;
                rangeEnd = segments[0].RelativeEnd;
            }
            else
            {
                text = fullText;
                rangeStart = TimeSpan.Zero;
                rangeEnd = clipDuration;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("ASR 未识别出任何文本内容。");
            }

            segments = SplitIntoSentences(text.Trim(), rangeStart, rangeEnd);
        }
        else if (string.IsNullOrWhiteSpace(fullText))
        {
            fullText = string.Join(" ", segments.Select(segment => segment.Text));
        }

        return new AsrTranscriptResult(clipStart, clipEnd, fullText, segments, providerLabel, createdAtUtc);
    }

    /// <summary>
    /// 把一整段文本按句末标点切成多句，并按字符数比例把 [rangeStart, rangeEnd] 的时间分配给各句。
    /// 没有标点的短文本保持单段；超长无标点文本按固定长度切块，保证长口播仍可逐句跳转。
    /// </summary>
    internal static List<AsrTranscriptSegment> SplitIntoSentences(string text, TimeSpan rangeStart, TimeSpan rangeEnd)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            current.Append(ch);

            // 「……」是同一个省略号，不能在两个字符之间断开。
            if (ch == '…' && i + 1 < text.Length && text[i + 1] == '…')
            {
                continue;
            }

            bool hardBreak = ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '…' or '\n';
            bool softBreak = ch is '，' or ',' or '、' or '：' or ':';
            if (hardBreak ||
                (softBreak && current.Length >= MaxEstimatedSentenceChars) ||
                current.Length >= MaxEstimatedSentenceChars * 2)
            {
                FlushSentencePiece(pieces, current);
            }
        }

        FlushSentencePiece(pieces, current);
        if (pieces.Count == 1 && pieces[0].Length > FallbackChunkChars)
        {
            // 整段口播没有任何标点（部分模型不输出标点）：按固定长度切块，避免只剩一整行。
            var chunked = new List<string>();
            for (int index = 0; index < pieces[0].Length; index += FallbackChunkChars)
            {
                chunked.Add(pieces[0].Substring(index, Math.Min(FallbackChunkChars, pieces[0].Length - index)));
            }

            pieces = chunked;
        }

        if (pieces.Count <= 1)
        {
            return new List<AsrTranscriptSegment>
            {
                new(rangeStart, rangeEnd, text),
            };
        }

        var duration = rangeEnd - rangeStart;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        int totalLength = pieces.Sum(piece => piece.Length);
        var result = new List<AsrTranscriptSegment>(pieces.Count);
        long consumed = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            long startOffset = totalLength == 0
                ? 0
                : (long)Math.Round(duration.Ticks * (double)consumed / totalLength);
            consumed += pieces[i].Length;
            long endOffset = totalLength == 0
                ? 0
                : (long)Math.Round(duration.Ticks * (double)consumed / totalLength);
            var start = rangeStart + TimeSpan.FromTicks(Math.Clamp(startOffset, 0, duration.Ticks));
            var end = rangeStart + TimeSpan.FromTicks(Math.Clamp(endOffset, startOffset, duration.Ticks));
            if (end < start)
            {
                end = start;
            }

            result.Add(new AsrTranscriptSegment(start, end, pieces[i]));
        }

        // 最后一句严格覆盖到片段末尾，避免累计误差留下空白。
        result[^1] = result[^1] with { RelativeEnd = rangeEnd };
        return result;
    }

    /// <summary>
    /// 旧缓存里纯文本响应只存了一整段：读取时按句补拆（不重新请求、不消耗额度），
    /// 让历史结果也能逐句点击跳转。
    /// </summary>
    internal static AsrTranscriptResult UpgradeCachedResult(AsrTranscriptResult cached)
    {
        if (cached.Segments is { Count: > 1 })
        {
            return cached;
        }

        string text = string.IsNullOrWhiteSpace(cached.FullText)
            ? cached.Segments is { Count: 1 } ? cached.Segments[0].Text : string.Empty
            : cached.FullText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return cached;
        }

        var duration = cached.ClipEnd - cached.ClipStart;
        var expanded = SplitIntoSentences(text.Trim(), TimeSpan.Zero, duration);
        return expanded.Count > 1 ? cached with { Segments = expanded } : cached;
    }

    private static void FlushSentencePiece(List<string> pieces, StringBuilder buffer)
    {
        string piece = buffer.ToString().Trim();
        buffer.Clear();
        if (piece.Length > 0)
        {
            pieces.Add(piece);
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            max = min;
        }

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
