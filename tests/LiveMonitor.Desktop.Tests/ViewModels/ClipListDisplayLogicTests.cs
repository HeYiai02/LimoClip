using LiveMonitor.Core.Entities;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipListDisplayLogicTests
{
    [Theory]
    [InlineData(-1, "00:00")]
    [InlineData(0, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(90, "01:30")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    public void FormatTime_ProducesSecondPrecision(int seconds, string expected)
    {
        Assert.Equal(expected, ClipListDisplayLogic.FormatTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void RoomTitle_EmptyDisplayName_FallsBackToRoomId()
    {
        Assert.Equal("房间 abc123", ClipListDisplayLogic.RoomTitle(null, "abc123"));
        Assert.Equal("房间 abc123", ClipListDisplayLogic.RoomTitle("   ", "abc123"));
    }

    [Fact]
    public void RoomTitle_NormalDisplayName_IsPreserved()
    {
        Assert.Equal("测试房间", ClipListDisplayLogic.RoomTitle("测试房间", "abc123"));
    }

    [Fact]
    public void BuildSessionDisplayText_Live_HasBadgeAndRecordedDuration()
    {
        var text = ClipListDisplayLogic.BuildSessionDisplayText(true, "10:00:00", "测试房间", 125);

        Assert.Equal("🔴 录制中 · 测试房间 (已录 02:05)", text);
    }

    [Fact]
    public void BuildSessionDisplayText_NotLive_ShowsStartedAtAndTitle()
    {
        var text = ClipListDisplayLogic.BuildSessionDisplayText(false, "10:00:00", "测试房间", 125);

        Assert.Equal("10:00:00 · 测试房间", text);
    }

    [Fact]
    public void BuildSessionDetailText_Live_HasRecordedPrefix()
    {
        var text = ClipListDisplayLogic.BuildSessionDetailText(true, 125, 42, 3);

        Assert.Equal("已录 02:05 · 弹幕 42 条 · 高能 3 个", text);
    }

    [Fact]
    public void BuildSessionDetailText_NotLive_HasNoPrefix()
    {
        var text = ClipListDisplayLogic.BuildSessionDetailText(false, 125, 42, 3);

        Assert.Equal("02:05 · 弹幕 42 条 · 高能 3 个", text);
    }

    [Theory]
    [InlineData(0, "0.0/s")]
    [InlineData(2.34, "2.3/s")]
    [InlineData(2.36, "2.4/s")]
    [InlineData(10.0, "10.0/s")]
    public void FormatRate_FormatsOneDecimal(double rate, string expected)
    {
        Assert.Equal(expected, ClipListDisplayLogic.FormatRate(rate));
    }

    [Fact]
    public void FormatBaselineRate_HasPrefix()
    {
        Assert.Equal("基线 1.5/s", ClipListDisplayLogic.FormatBaselineRate(1.5));
    }

    [Fact]
    public void BuildHighlightToolTip_UsesParenthesesAndNewLine()
    {
        var text = ClipListDisplayLogic.BuildHighlightToolTip("01:05", "3.2/s", "基线 1.5/s", "弹幕速率突增");

        Assert.Equal($"01:05 · 3.2/s（基线 1.5/s）{Environment.NewLine}弹幕速率突增", text);
    }

    [Theory]
    [InlineData(true, "转写中…")]
    [InlineData(false, "转写")]
    public void TranscribeButtonText_DependsOnRunningState(bool isTranscribing, string expected)
    {
        Assert.Equal(expected, ClipListDisplayLogic.TranscribeButtonText(isTranscribing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAsrSummary_EmptyText_ReturnsPlaceholder(string? fullText)
    {
        Assert.Equal("（未识别到文本）", ClipListDisplayLogic.BuildAsrSummary(fullText));
    }

    [Fact]
    public void BuildAsrSummary_TrimsAndKeepsShortText()
    {
        Assert.Equal("测试文本", ClipListDisplayLogic.BuildAsrSummary("  测试文本  "));
    }

    [Fact]
    public void BuildAsrSummary_ExactlyMaxLength_IsNotTruncated()
    {
        var text = new string('a', ClipListDisplayLogic.AsrSummaryMaxLength);

        Assert.Equal(text, ClipListDisplayLogic.BuildAsrSummary(text));
    }

    [Fact]
    public void BuildAsrSummary_OverMaxLength_TruncatesWithEllipsis()
    {
        var text = new string('a', ClipListDisplayLogic.AsrSummaryMaxLength + 1);

        var summary = ClipListDisplayLogic.BuildAsrSummary(text);

        Assert.Equal(ClipListDisplayLogic.AsrSummaryMaxLength + 1, summary.Length);
        Assert.EndsWith("…", summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildAsrSummaryToolTip_EmptyText_ReturnsPlaceholder(string? fullText)
    {
        Assert.Equal("（未识别到文本）", ClipListDisplayLogic.BuildAsrSummaryToolTip(fullText));
    }

    [Fact]
    public void BuildAsrSummaryToolTip_KeepsFullTextWithoutTruncation()
    {
        var text = new string('a', ClipListDisplayLogic.AsrSummaryMaxLength + 20);

        Assert.Equal(text, ClipListDisplayLogic.BuildAsrSummaryToolTip($"  {text}  "));
    }

    [Fact]
    public void ClipExportTaskViewModel_FormatTime_DelegatesToLogic()
    {
        Assert.Equal("01:05", ClipExportTaskViewModel.FormatTime(TimeSpan.FromSeconds(65)));
    }

    [Fact]
    public void ClipSessionOption_NonLive_DelegatesDisplayAndDetailText()
    {
        var session = new RecordingSession
        {
            RoomDisplayName = "测试房间",
            DurationSeconds = 125,
            DanmakuCount = 42,
            HighlightCount = 3
        };
        var option = new ClipSessionOption(session);

        Assert.Equal("测试房间", option.RoomTitle);
        Assert.Equal("02:05 · 弹幕 42 条 · 高能 3 个", option.DetailText);
    }
}
