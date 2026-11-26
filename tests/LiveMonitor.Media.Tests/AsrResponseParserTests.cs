using LiveMonitor.Core.Models;
using LiveMonitor.Media.Asr;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class AsrResponseParserTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 26, 8, 30, 0, TimeSpan.Zero);

    private static AsrTranscriptResult Parse(string json, TimeSpan clipStart, TimeSpan clipEnd)
        => AsrResponseParser.Parse(json, clipStart, clipEnd, "groq (whisper-large-v3-turbo)", CreatedAt);

    [Fact]
    public void Verbose_json_maps_segments_and_skips_blank_text()
    {
        const string json = """
            {
              "text": " 你好世界。第二句话。 ",
              "segments": [
                { "start": 0.0, "end": 1.5, "text": " 你好世界。 " },
                { "start": 1.5, "end": 2.0, "text": "   " },
                { "start": 2.0, "end": 3.2, "text": "第二句话。" }
              ]
            }
            """;

        var result = Parse(json, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, result.ClipStart);
        Assert.Equal(TimeSpan.FromSeconds(10), result.ClipEnd);
        Assert.Equal("你好世界。第二句话。", result.FullText);
        Assert.Equal("groq (whisper-large-v3-turbo)", result.ProviderLabel);
        Assert.Equal(CreatedAt, result.CreatedAtUtc);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Segments[0].RelativeEnd - result.Segments[0].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Segments[1].RelativeStart);
        Assert.Equal("第二句话。", result.Segments[1].Text);
    }

    [Fact]
    public void Segment_times_are_clamped_into_clip_range()
    {
        const string json = """
            { "text": "", "segments": [
              { "start": -3.0, "end": 2.0, "text": "a" },
              { "start": 4.0, "end": 99.0, "text": "b" }
            ] }
            """;

        var result = Parse(json, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(40));

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(TimeSpan.Zero, result.Segments[0].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Segments[0].RelativeEnd);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Segments[1].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Segments[1].RelativeEnd);
        Assert.Equal("a b", result.FullText);
    }

    [Fact]
    public void Segment_missing_end_uses_start()
    {
        const string json = """
            { "text": "", "segments": [ { "start": 3.0, "text": "没有结束时间" } ] }
            """;

        var result = Parse(json, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimeSpan.FromSeconds(3), segment.RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(3), segment.RelativeEnd);
        Assert.Single(result.Segments);
    }

    [Fact]
    public void Segment_text_is_used_when_full_text_is_missing()
    {
        const string json = """
            { "segments": [ { "start": 0.0, "end": 2.0, "text": "只有分段" } ] }
            """;

        var result = Parse(json, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        var segment = Assert.Single(result.Segments);
        Assert.Equal("只有分段", segment.Text);
        Assert.Equal(string.Empty, result.FullText);
    }

    [Fact]
    public void Plain_text_response_is_split_into_sentences_with_timeline()
    {
        const string json = """{ "text": "第一句。第二句！第三句？" }""";

        var result = Parse(json, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(35));

        Assert.Equal("第一句。第二句！第三句？", result.FullText);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(TimeSpan.Zero, result.Segments[0].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Segments[^1].RelativeEnd);
        for (int i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].RelativeEnd, result.Segments[i].RelativeStart);
        }
    }

    [Fact]
    public void Single_segment_is_split_within_its_own_range()
    {
        const string json = """
            { "text": "整段话。", "segments": [ { "start": 2.0, "end": 6.0, "text": "前半句。后半句。" } ] }
            """;

        var result = Parse(json, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Segments[0].RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Segments[^1].RelativeEnd);
        Assert.Equal("整段话。", result.FullText);
        Assert.Equal("前半句。", result.Segments[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_response_is_rejected(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json, TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Equal("ASR API 返回了空响应。", ex.Message);
    }

    [Fact]
    public void Non_object_json_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("[1,2,3]", TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Equal("ASR 响应不是 JSON 对象。", ex.Message);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("{not-json", TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Equal("ASR 响应不是合法 JSON，无法解析。", ex.Message);
    }

    [Fact]
    public void Whitespace_only_transcript_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Parse("""{ "text": "   ", "segments": [] }""", TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        Assert.Equal("ASR 未识别出任何文本内容。", ex.Message);
    }

    [Fact]
    public void Hard_break_punctuation_splits_into_sentences()
    {
        var segments = AsrResponseParser.SplitIntoSentences(
            "第一句话。第二句话！第三句话？第四句话；第五句话;",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

        Assert.Equal(5, segments.Count);
        Assert.Equal("第一句话。", segments[0].Text);
        Assert.Equal("第五句话;", segments[^1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), segments[^1].RelativeEnd);
    }

    [Fact]
    public void Latin_punctuation_and_newline_split_into_sentences()
    {
        var segments = AsrResponseParser.SplitIntoSentences(
            "hello! world? end;\nnext line",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(8));

        Assert.Equal(4, segments.Count);
        Assert.Equal("hello!", segments[0].Text);
        Assert.Equal("next line", segments[^1].Text);
    }

    [Fact]
    public void Ellipsis_is_not_split_between_its_two_characters()
    {
        var segments = AsrResponseParser.SplitIntoSentences(
            "等等……然后呢。",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4));

        Assert.Equal(2, segments.Count);
        Assert.Equal("等等……", segments[0].Text);
        Assert.Equal("然后呢。", segments[1].Text);
    }

    [Fact]
    public void Soft_break_only_splits_after_long_enough_piece()
    {
        var text = new string('甲', 46) + "，乙";

        var segments = AsrResponseParser.SplitIntoSentences(text, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        var segment = Assert.Single(segments);
        Assert.Equal(text, segment.Text);
    }

    [Fact]
    public void Soft_break_splits_when_piece_reaches_threshold()
    {
        var text = new string('甲', 48) + "，乙";

        var segments = AsrResponseParser.SplitIntoSentences(text, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.Equal(2, segments.Count);
        Assert.Equal(new string('甲', 48) + "，", segments[0].Text);
        Assert.Equal("乙", segments[1].Text);
    }

    [Fact]
    public void Long_text_without_punctuation_is_chunked_by_fixed_length()
    {
        var text = new string('乙', 120);

        var segments = AsrResponseParser.SplitIntoSentences(text, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.Equal(2, segments.Count);
        Assert.Equal(96, segments[0].Text.Length);
        Assert.Equal(24, segments[^1].Text.Length);
        Assert.Equal(TimeSpan.FromSeconds(30), segments[^1].RelativeEnd);
    }

    [Fact]
    public void Long_text_below_double_threshold_is_chunked_by_fallback_length()
    {
        var text = new string('丁', 60);

        var segments = AsrResponseParser.SplitIntoSentences(text, TimeSpan.Zero, TimeSpan.FromSeconds(20));

        Assert.Equal(2, segments.Count);
        Assert.Equal(48, segments[0].Text.Length);
        Assert.Equal(12, segments[^1].Text.Length);
        Assert.Equal(TimeSpan.FromSeconds(20), segments[^1].RelativeEnd);
    }

    [Fact]
    public void Short_text_without_punctuation_stays_single_segment()
    {
        var text = new string('丙', 48);

        var segments = AsrResponseParser.SplitIntoSentences(text, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(12));

        var segment = Assert.Single(segments);
        Assert.Equal(text, segment.Text);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.RelativeStart);
        Assert.Equal(TimeSpan.FromSeconds(12), segment.RelativeEnd);
    }

    [Fact]
    public void Empty_text_returns_single_empty_segment()
    {
        var segments = AsrResponseParser.SplitIntoSentences(string.Empty, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var segment = Assert.Single(segments);
        Assert.Equal(string.Empty, segment.Text);
        Assert.Equal(TimeSpan.FromSeconds(5), segment.RelativeEnd);
    }

    [Fact]
    public void Sentence_timeline_is_proportional_to_character_count()
    {
        var segments = AsrResponseParser.SplitIntoSentences(
            "短。很长很长很长很长的句子。",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12));

        Assert.Equal(2, segments.Count);
        var first = segments[0].RelativeEnd - segments[0].RelativeStart;
        var second = segments[1].RelativeEnd - segments[1].RelativeStart;
        Assert.Equal(TimeSpan.FromSeconds(12), segments[^1].RelativeEnd);
        Assert.True(first < second);
        Assert.Equal(TimeSpan.Zero, segments[0].RelativeStart);
    }

    [Fact]
    public void Negative_range_yields_zero_length_segments()
    {
        var segments = AsrResponseParser.SplitIntoSentences(
            "一。二。三。",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));

        Assert.Equal(3, segments.Count);
        Assert.All(segments, segment => Assert.Equal(TimeSpan.FromSeconds(5), segment.RelativeStart));
        Assert.Equal(TimeSpan.FromSeconds(5), segments[0].RelativeEnd);
        Assert.Equal(TimeSpan.FromSeconds(5), segments[1].RelativeEnd);
        Assert.Equal(TimeSpan.FromSeconds(2), segments[^1].RelativeEnd);
    }

    [Fact]
    public void Cached_result_with_multiple_segments_is_returned_unchanged()
    {
        var cached = new AsrTranscriptResult(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            "已有",
            new List<AsrTranscriptSegment>
            {
                new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "已有"),
                new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "分段"),
            },
            "label",
            CreatedAt);

        var upgraded = AsrResponseParser.UpgradeCachedResult(cached);

        Assert.Same(cached, upgraded);
    }

    [Fact]
    public void Cached_single_segment_is_expanded_by_full_text()
    {
        var cached = new AsrTranscriptResult(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(13),
            "第一句。第二句。",
            new List<AsrTranscriptSegment>
            {
                new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "第一句。第二句。"),
            },
            "label",
            CreatedAt);

        var upgraded = AsrResponseParser.UpgradeCachedResult(cached);

        Assert.Equal(2, upgraded.Segments.Count);
        Assert.Equal("第一句。", upgraded.Segments[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(3), upgraded.ClipStart);
        Assert.Equal(TimeSpan.FromSeconds(13), upgraded.ClipEnd);
    }

    [Fact]
    public void Cached_single_segment_without_full_text_uses_segment_text()
    {
        var cached = new AsrTranscriptResult(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            string.Empty,
            new List<AsrTranscriptSegment>
            {
                new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "甲。乙。"),
            },
            "label",
            CreatedAt);

        var upgraded = AsrResponseParser.UpgradeCachedResult(cached);

        Assert.Equal(2, upgraded.Segments.Count);
        Assert.Equal("甲。", upgraded.Segments[0].Text);
    }

    [Fact]
    public void Cached_empty_text_is_returned_unchanged()
    {
        var cached = new AsrTranscriptResult(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            string.Empty,
            Array.Empty<AsrTranscriptSegment>(),
            "label",
            CreatedAt);

        var upgraded = AsrResponseParser.UpgradeCachedResult(cached);

        Assert.Same(cached, upgraded);
        Assert.Empty(upgraded.Segments);
    }

    [Fact]
    public void Cached_result_that_cannot_be_split_is_returned_unchanged()
    {
        var cached = new AsrTranscriptResult(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            "一整段没有标点的短文本",
            new List<AsrTranscriptSegment>
            {
                new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "一整段没有标点的短文本"),
            },
            "label",
            CreatedAt);

        var upgraded = AsrResponseParser.UpgradeCachedResult(cached);

        Assert.Same(cached, upgraded);
    }

    [Fact]
    public void Constants_match_provider_contract()
    {
        Assert.Equal(48, AsrResponseParser.MaxEstimatedSentenceChars);
        Assert.Equal(48, AsrResponseParser.FallbackChunkChars);
    }
}
