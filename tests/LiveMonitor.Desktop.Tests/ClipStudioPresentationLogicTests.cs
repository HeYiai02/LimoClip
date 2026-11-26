using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 剪辑台展示层纯函数单测：文案拼装、时间/大小格式化、高能投影与弹幕转换。
/// 不触碰 Avalonia、播放器、数据库与 ffmpeg。
/// </summary>
public sealed class ClipStudioPresentationLogicTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- BuildVideoSubtitle ----

    [Fact]
    public void BuildVideoSubtitle_WithoutSession_ReturnsGeometry()
    {
        var subtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(null, Metadata());

        Assert.Equal("1920x1080 · 30fps · H264", subtitle);
    }

    [Fact]
    public void BuildVideoSubtitle_FractionalFps_UsesTwoDecimals()
    {
        var subtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(null, Metadata(fps: 29.97));

        Assert.Equal("1920x1080 · 29.97fps · H264", subtitle);
    }

    [Fact]
    public void BuildVideoSubtitle_EmptyCodec_FallsBackToH264()
    {
        var subtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(null, Metadata(codec: "  "));

        Assert.Equal("1920x1080 · 30fps · h264", subtitle);
    }

    [Fact]
    public void BuildVideoSubtitle_WithSession_IncludesRoomAndTitle()
    {
        var subtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(
            Session(room: "#1 测试房", title: "直播标题"),
            Metadata());

        Assert.Equal("#1 测试房 · 直播标题 · 1920x1080 · 30fps · H264", subtitle);
    }

    [Fact]
    public void BuildVideoSubtitle_EmptyStreamTitle_UsesLocalRecording()
    {
        var subtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(
            Session(room: "#2 房间", title: " "),
            Metadata());

        Assert.Equal("#2 房间 · 本地录像 · 1920x1080 · 30fps · H264", subtitle);
    }

    // ---- BuildLiveVideoSubtitle ----

    [Fact]
    public void BuildLiveVideoSubtitle_WithMetadata_UsesPartAndGeometry()
    {
        var subtitle = ClipStudioPresentationLogic.BuildLiveVideoSubtitle(
            Session(room: "#1 测试房"),
            Live(partNumber: 2, fileSizeBytes: 1024),
            Metadata(width: 1280, height: 720, fps: 60, codec: "hevc"));

        Assert.Equal("#1 测试房 · 录制中 · 分片 2 · 1280x720 · 60fps · HEVC", subtitle);
    }

    [Fact]
    public void BuildLiveVideoSubtitle_WithoutMetadata_FallsBackToSize()
    {
        var subtitle = ClipStudioPresentationLogic.BuildLiveVideoSubtitle(
            Session(room: "#1 测试房"),
            Live(partNumber: 3, fileSizeBytes: 1_572_864),
            metadata: null);

        Assert.Equal("#1 测试房 · 录制中 · 分片 3（1.5 MB）", subtitle);
    }

    [Fact]
    public void BuildLiveVideoSubtitle_ZeroGeometry_FallsBackToSize()
    {
        var subtitle = ClipStudioPresentationLogic.BuildLiveVideoSubtitle(
            Session(room: "#1 测试房"),
            Live(partNumber: 1, fileSizeBytes: 500),
            Metadata(width: 0, height: 0));

        Assert.Equal("#1 测试房 · 录制中 · 分片 1（500 B）", subtitle);
    }

    // ---- FindCompanionDanmaku ----

    [Fact]
    public void FindCompanionDanmaku_ExistingJsonl_ReturnsPath()
    {
        var dir = Directory.CreateTempSubdirectory("limoclip-presentation-");
        try
        {
            var video = Path.Combine(dir.FullName, "video.mkv");
            var danmaku = Path.Combine(dir.FullName, "video.jsonl");
            File.WriteAllText(video, "video");
            File.WriteAllText(danmaku, "{}");

            var found = ClipStudioPresentationLogic.FindCompanionDanmaku(video);

            Assert.Equal(danmaku, found);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindCompanionDanmaku_MissingJsonl_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("limoclip-presentation-");
        try
        {
            var video = Path.Combine(dir.FullName, "video.mkv");
            File.WriteAllText(video, "video");

            Assert.Null(ClipStudioPresentationLogic.FindCompanionDanmaku(video));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindCompanionDanmaku_PathWithoutExtension_UsesAppendedSuffix()
    {
        var dir = Directory.CreateTempSubdirectory("limoclip-presentation-");
        try
        {
            var videoWithoutExtension = Path.Combine(dir.FullName, "video");
            var danmaku = videoWithoutExtension + ".jsonl";
            File.WriteAllText(danmaku, "{}");

            Assert.Equal(danmaku, ClipStudioPresentationLogic.FindCompanionDanmaku(videoWithoutExtension));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Clamp ----

    [Theory]
    [InlineData(5, 0, 10, 5)]
    [InlineData(-1, 0, 10, 0)]
    [InlineData(11, 0, 10, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(10, 0, 10, 10)]
    public void Clamp_ClampsToRange(int valueSeconds, int minSeconds, int maxSeconds, int expectedSeconds)
    {
        var clamped = ClipStudioPresentationLogic.Clamp(
            TimeSpan.FromSeconds(valueSeconds),
            TimeSpan.FromSeconds(minSeconds),
            TimeSpan.FromSeconds(maxSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), clamped);
    }

    // ---- BuildLiveStatusText ----

    [Fact]
    public void BuildLiveStatusText_WithSafeDuration_IncludesBothClocks()
    {
        var text = ClipStudioPresentationLogic.BuildLiveStatusText(
            TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(30));

        Assert.Equal("🔴 正在录制 · 已录 01:15（安全可剪至 00:30）", text);
    }

    [Fact]
    public void BuildLiveStatusText_WithoutSafeDuration_ShowsGenerating()
    {
        var text = ClipStudioPresentationLogic.BuildLiveStatusText(
            TimeSpan.FromSeconds(65),
            TimeSpan.Zero);

        Assert.Equal("🔴 正在录制 · 已录 01:05（安全缓冲生成中）", text);
    }

    [Fact]
    public void BuildLiveStatusText_OverOneHour_FormatsHours()
    {
        var text = ClipStudioPresentationLogic.BuildLiveStatusText(
            TimeSpan.FromSeconds(3725),
            TimeSpan.FromSeconds(3700));

        Assert.Contains("已录 01:02:05", text);
        Assert.Contains("安全可剪至 01:01:40", text);
    }

    // ---- FormatFileSize ----

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(-5L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_073_741_824L, "1 GB")]
    [InlineData(2_199_023_255_552L, "2 TB")]
    public void FormatFileSize_FormatsUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ClipStudioPresentationLogic.FormatFileSize(bytes));
    }

    // ---- ProjectHighlights ----

    [Fact]
    public void ProjectHighlights_RecordingOffset_TakesPrecedence()
    {
        var events = new[] { Highlight(SessionStart.AddSeconds(100), offsetMs: 1500) };

        var projections = ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            TimeSpan.FromSeconds(10));

        var projection = Assert.Single(projections);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), projection.Position);
        Assert.Same(events[0], projection.Event);
    }

    [Fact]
    public void ProjectHighlights_MissingOffset_UsesTriggerTimeMinusStart()
    {
        var events = new[] { Highlight(SessionStart.AddMilliseconds(5500), offsetMs: null) };

        var projection = Assert.Single(ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromMilliseconds(5500), projection.Position);
    }

    [Fact]
    public void ProjectHighlights_NegativePosition_ClampedToZero()
    {
        var events = new[] { Highlight(SessionStart.AddSeconds(-3), offsetMs: null) };

        var projection = Assert.Single(ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.Zero, projection.Position);
    }

    [Fact]
    public void ProjectHighlights_BeyondDuration_ClampedToDuration()
    {
        var duration = TimeSpan.FromSeconds(10);
        var events = new[] { Highlight(SessionStart.AddSeconds(100), offsetMs: 90_000) };

        var projection = Assert.Single(ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            duration));

        Assert.Equal(duration, projection.Position);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    public void ProjectHighlights_NonPositiveOffset_FallsBackToTriggerTime(long offsetMs)
    {
        var events = new[]
        {
            Highlight(SessionStart.AddSeconds(4), offsetMs)
        };

        var projection = Assert.Single(ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(4), projection.Position);
    }

    [Fact]
    public void ProjectHighlights_Empty_ReturnsEmpty()
    {
        var projections = ClipStudioPresentationLogic.ProjectHighlights(
            Array.Empty<HighlightEvent>(),
            SessionStart,
            TimeSpan.FromSeconds(10));

        Assert.Empty(projections);
    }

    [Fact]
    public void ProjectHighlights_PreservesOrderAndReason()
    {
        var events = new[]
        {
            Highlight(SessionStart, offsetMs: 3000, reason: "第一条"),
            Highlight(SessionStart, offsetMs: 1000, reason: "第二条")
        };

        var projections = ClipStudioPresentationLogic.ProjectHighlights(
            events,
            SessionStart,
            TimeSpan.FromSeconds(10));

        Assert.Equal(2, projections.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), projections[0].Position);
        Assert.Equal("第一条", projections[0].Event.TriggerReason);
        Assert.Equal(TimeSpan.FromSeconds(1), projections[1].Position);
        Assert.Equal("第二条", projections[1].Event.TriggerReason);
    }

    // ---- ToDanmakuMessages ----

    [Fact]
    public void ToDanmakuMessages_MapsFieldsAndStableIds()
    {
        var timestamp = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            new RecordedDanmakuItem(0, "用户A", "普通弹幕", IsSticker: false, IsGift: false),
            new RecordedDanmakuItem(1234, "用户B", "[表情]", IsSticker: true, IsGift: true)
        };

        var messages = ClipStudioPresentationLogic.ToDanmakuMessages(
            items,
            PlatformType.Bilibili,
            "100",
            timestamp);

        Assert.Equal(2, messages.Count);
        var first = messages[0];
        Assert.Equal(PlatformType.Bilibili, first.Platform);
        Assert.Equal("100", first.RoomId);
        Assert.Equal("rec-0", first.SenderUid);
        Assert.Equal("用户A", first.SenderName);
        Assert.Equal("普通弹幕", first.Content);
        Assert.Equal(timestamp, first.Timestamp);
        Assert.False(first.IsGiftOrSuperChat);
        Assert.False(first.IsSticker);

        var second = messages[1];
        Assert.Equal("rec-1234", second.SenderUid);
        Assert.True(second.IsGiftOrSuperChat);
        Assert.True(second.IsSticker);
    }

    [Fact]
    public void ToDanmakuMessages_Empty_ReturnsEmpty()
    {
        var messages = ClipStudioPresentationLogic.ToDanmakuMessages(
            Array.Empty<RecordedDanmakuItem>(),
            PlatformType.CustomStream,
            "local-clip",
            SessionStart);

        Assert.Empty(messages);
    }

    // ---- helpers ----

    private static VideoMetadata Metadata(
        int width = 1920,
        int height = 1080,
        double fps = 30,
        string codec = "h264")
        => new(TimeSpan.FromMinutes(1), width, height, fps, codec);

    private static RecordingSession Session(string room = "#1 测试房", string title = "直播标题")
        => new()
        {
            RoomDisplayName = room,
            StreamTitle = title,
            RoomId = "100",
            Platform = PlatformType.Bilibili
        };

    private static LiveRecordingInfo Live(int partNumber = 1, long fileSizeBytes = 0)
        => new(
            SessionId: Guid.NewGuid(),
            LiveRoomId: Guid.NewGuid(),
            RoomDisplayNumber: 1,
            RoomDisplayName: "#1 测试房",
            PartFilePath: @"C:\videos\session.part001.mkv",
            PartNumber: partNumber,
            CompletedDuration: TimeSpan.Zero,
            RecordedDuration: TimeSpan.FromSeconds(5),
            FileSizeBytes: fileSizeBytes,
            DanmakuCount: 0,
            HighlightCount: 0);

    private static HighlightEvent Highlight(
        DateTimeOffset triggerTime,
        long? offsetMs = null,
        string reason = "弹幕激增")
        => new(
            PlatformType.Bilibili,
            "100",
            triggerTime,
            CurrentRatePerSec: 5d,
            BaselineRatePerSec: 1d,
            TriggerReason: reason)
        {
            RecordingOffsetMs = offsetMs
        };

    [Fact]
    public void BuildPositionText_formats_position_and_duration()
        => Assert.Equal("00:05 / 01:05", ClipStudioPresentationLogic.BuildPositionText(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65)));

    [Fact]
    public void BuildSelectionText_returns_zero_when_no_video()
        => Assert.Equal("00:00", ClipStudioPresentationLogic.BuildSelectionText(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));

    [Fact]
    public void BuildSelectionText_formats_selection_duration()
        => Assert.Equal("01:00", ClipStudioPresentationLogic.BuildSelectionText(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65)));

    [Fact]
    public void BuildDanmakuInfoText_handles_missing_file()
    {
        Assert.Equal("无伴随弹幕文件", ClipStudioPresentationLogic.BuildDanmakuInfoText(null, isLive: false));
        Assert.Equal("弹幕录制中…", ClipStudioPresentationLogic.BuildDanmakuInfoText(null, isLive: true));
    }

    [Fact]
    public void BuildDanmakuInfoText_counts_items_and_marks_live()
    {
        var items = new List<RecordedDanmakuItem>
        {
            new(100, "a", "x", false, false),
            new(200, "b", "y", false, false),
        };
        Assert.Equal("弹幕 2 条", ClipStudioPresentationLogic.BuildDanmakuInfoText(items, isLive: false));
        Assert.Equal("弹幕 2 条 · 录制中", ClipStudioPresentationLogic.BuildDanmakuInfoText(items, isLive: true));
    }

    [Fact]
    public void BuildLiveVideoTitle_combines_room_and_state()
    {
        var session = new RecordingSession { RoomDisplayName = "房间A" };
        Assert.Equal("房间A · 录制中", ClipStudioPresentationLogic.BuildLiveVideoTitle(session));
    }

    [Theory]
    [InlineData(true, "⏸")]
    [InlineData(false, "▶")]
    public void PlayPauseGlyph_maps_state(bool playing, string expected)
        => Assert.Equal(expected, ClipStudioPresentationLogic.PlayPauseGlyph(playing));
}
