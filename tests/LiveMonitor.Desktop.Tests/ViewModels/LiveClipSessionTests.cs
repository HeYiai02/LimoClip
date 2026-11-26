using System;
using System.Linq;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class LiveClipSessionTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RoomId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string PartPath = @"E:\live-cache\room\room.part001.mkv";

    private static LiveRecordingInfo Info(double recordedSeconds, int partNumber = 1, string partPath = PartPath)
        => new(
            SessionId,
            RoomId,
            1,
            "#1 测试房间",
            partPath,
            partNumber,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(recordedSeconds),
            0,
            0,
            0);

    private static RecordedDanmakuItem Item(long offsetMs, string text = "弹幕")
        => new(offsetMs, "用户", text, IsSticker: false, IsGift: false);

    [Fact]
    public void Start_SetsInfoPartPathAndResetsCursors()
    {
        var session = new LiveClipSession();

        session.Start(Info(65), PartPath);

        Assert.True(session.IsActive);
        Assert.Equal(SessionId, session.Info!.SessionId);
        Assert.Equal(PartPath, session.PartPath);
        Assert.Equal(0, session.DanmakuOffset);
        Assert.Equal(TimeSpan.FromSeconds(65), session.RecordedDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), session.SafeClipDuration);
        Assert.Equal(TimeSpan.Zero, session.PeaksExtractedUntil);
    }

    [Fact]
    public void Start_StatusTextContainsRecordingAndSafeDeadline()
    {
        var session = new LiveClipSession();

        session.Start(Info(65), PartPath);

        Assert.Contains("正在录制", session.StatusText, StringComparison.Ordinal);
        Assert.Contains("01:05", session.StatusText, StringComparison.Ordinal);
        Assert.Contains("安全可剪至 01:00", session.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_ShortRecording_HasNoSafeClipRange()
    {
        var session = new LiveClipSession();

        session.Start(Info(3), PartPath);

        Assert.Equal(TimeSpan.Zero, session.SafeClipDuration);
        Assert.Contains("安全缓冲生成中", session.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_JustOverMargin_SafeDurationIsRemainder()
    {
        var session = new LiveClipSession();

        session.Start(Info(6), PartPath);

        Assert.Equal(TimeSpan.FromSeconds(1), session.SafeClipDuration);
        Assert.Contains("安全可剪至 00:01", session.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_NegativeRecordedDuration_ClampsToZero()
    {
        var session = new LiveClipSession();

        session.Start(Info(-5), PartPath);

        Assert.Equal(TimeSpan.Zero, session.RecordedDuration);
        Assert.Equal(TimeSpan.Zero, session.SafeClipDuration);
    }

    [Fact]
    public void Start_NullInfo_Throws()
    {
        var session = new LiveClipSession();

        Assert.Throws<ArgumentNullException>(() => session.Start(null!, PartPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Start_BlankPartPath_Throws(string partPath)
    {
        var session = new LiveClipSession();

        Assert.Throws<ArgumentException>(() => session.Start(Info(10), partPath));
    }

    [Fact]
    public void Start_ResetsPreviousIncrementalState()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);
        session.AdviseDanmakuOffset(4096);
        session.AppendPeaks(new[] { 0.5f, 0.6f }, 2);
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(20));

        session.Start(Info(70), @"E:\live-cache\room\room.part001.mkv");

        Assert.Equal(0, session.DanmakuOffset);
        Assert.Equal(TimeSpan.Zero, session.PeaksExtractedUntil);
        var waveform = session.BuildWaveform(null);
        Assert.NotNull(waveform);
        Assert.All(waveform!.AudioPeaks, peak => Assert.Equal(0f, peak));
    }

    [Fact]
    public void ApplyLatest_UpdatesDurationsAndPartPath()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.ApplyLatest(Info(125, partPath: @"E:\live-cache\room\room.part001.mkv"), @"E:\live-cache\room\room.part001.mkv");

        Assert.Equal(TimeSpan.FromSeconds(125), session.RecordedDuration);
        Assert.Equal(TimeSpan.FromSeconds(120), session.SafeClipDuration);
        Assert.Contains("02:05", session.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyLatest_KeepsExistingPartPath_WhenNewPathBlank()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.ApplyLatest(Info(70), null);

        Assert.Equal(PartPath, session.PartPath);
    }

    [Fact]
    public void ApplyLatest_PreservesDanmakuOffsetAndPeaks()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);
        session.AdviseDanmakuOffset(1024);
        session.AppendPeaks(new[] { 1f }, 1);
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(10));

        session.ApplyLatest(Info(70), PartPath);

        Assert.Equal(1024, session.DanmakuOffset);
        Assert.Equal(TimeSpan.FromSeconds(10), session.PeaksExtractedUntil);
        var waveform = session.BuildWaveform(null);
        Assert.NotNull(waveform);
        Assert.True(waveform!.AudioPeaks[0] > 0);
    }

    [Fact]
    public void ApplyLatest_Null_Throws()
    {
        var session = new LiveClipSession();

        Assert.Throws<ArgumentNullException>(() => session.ApplyLatest(null!, null));
    }

    [Theory]
    [InlineData(1234L, 1234L)]
    [InlineData(-5L, 0L)]
    public void AdviseDanmakuOffset_ClampsNegative(long offset, long expected)
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.AdviseDanmakuOffset(offset);

        Assert.Equal(expected, session.DanmakuOffset);
    }

    [Fact]
    public void TryTakeWaveformWindow_WithoutSafeDuration_ReturnsFalse()
    {
        var session = new LiveClipSession();
        session.Start(Info(3), PartPath);

        Assert.False(session.TryTakeWaveformWindow(out _));
    }

    [Fact]
    public void TryTakeWaveformWindow_AlignsToHalfSecondGrid()
    {
        var session = new LiveClipSession();
        session.Start(Info(15.75), PartPath);

        Assert.True(session.TryTakeWaveformWindow(out var window));
        Assert.Equal(TimeSpan.FromSeconds(10.5), window.To);
        Assert.Equal(21, window.ExpectedCount);
    }

    [Fact]
    public void TryTakeWaveformWindow_AfterExtracted_ReturnsFalse()
    {
        var session = new LiveClipSession();
        session.Start(Info(15.75), PartPath);
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(10.5));

        Assert.False(session.TryTakeWaveformWindow(out _));
    }

    [Fact]
    public void TryTakeWaveformWindow_AfterNewProgress_ContinuesFromExtracted()
    {
        var session = new LiveClipSession();
        session.Start(Info(15.75), PartPath);
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(10.5));

        session.ApplyLatest(Info(20.75), PartPath);

        Assert.True(session.TryTakeWaveformWindow(out var window));
        Assert.Equal(TimeSpan.FromSeconds(15.5), window.To);
        Assert.Equal(10, window.ExpectedCount);
    }

    [Fact]
    public void AppendPeaks_PadsMissingSegmentsWithZeros()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.AppendPeaks(new[] { 0.25f, 0.5f }, expectedCount: 4);

        var waveform = session.BuildWaveform(null);
        Assert.NotNull(waveform);
        Assert.Equal(130, waveform!.AudioPeaks.Length);
        Assert.Equal(0.25f, waveform.AudioPeaks[0]);
        Assert.Equal(0.5f, waveform.AudioPeaks[1]);
        Assert.Equal(0f, waveform.AudioPeaks[2]);
        Assert.Equal(0f, waveform.AudioPeaks[3]);
    }

    [Fact]
    public void AppendPeaks_TruncatesExtraSegments()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.AppendPeaks(new[] { 0.1f, 0.2f, 0.3f }, expectedCount: 2);

        var waveform = session.BuildWaveform(null);
        Assert.NotNull(waveform);
        Assert.Equal(0.1f, waveform!.AudioPeaks[0]);
        Assert.Equal(0.2f, waveform.AudioPeaks[1]);
        Assert.Equal(0f, waveform.AudioPeaks[2]);
    }

    [Fact]
    public void AppendPeaks_NonPositiveExpected_PreservesExistingPeaks()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);
        session.AppendPeaks(new[] { 0.1f }, expectedCount: 1);

        session.AppendPeaks(new[] { 0.9f }, expectedCount: 0);

        var waveform = session.BuildWaveform(null);
        Assert.NotNull(waveform);
        Assert.Equal(0.1f, waveform!.AudioPeaks[0]);
        Assert.Equal(0f, waveform.AudioPeaks[1]);
    }

    [Fact]
    public void MarkPeaksExtracted_IsMonotonic()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.MarkPeaksExtracted(TimeSpan.FromSeconds(5));
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(5), session.PeaksExtractedUntil);
    }

    [Fact]
    public void BuildWaveform_NotActive_ReturnsNull()
    {
        var session = new LiveClipSession();

        Assert.Null(session.BuildWaveform(null));
    }

    [Fact]
    public void BuildWaveform_ZeroRecordedDuration_ReturnsNull()
    {
        var session = new LiveClipSession();
        session.Start(Info(0), PartPath);

        Assert.Null(session.BuildWaveform(null));
    }

    [Fact]
    public void BuildWaveform_BucketCountFollowsHalfSecondGrid()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);

        session.AppendPeaks(new[] { 1f }, 1);
        var waveform = session.BuildWaveform(null);

        Assert.NotNull(waveform);
        Assert.Equal(130, waveform!.AudioPeaks.Length);
        Assert.Equal(130, waveform.DanmakuDensity.Length);
        Assert.Equal(TimeSpan.FromSeconds(65), waveform.Duration);
    }

    [Fact]
    public void BuildWaveform_NormalizesDanmakuDensityPerBucket()
    {
        var session = new LiveClipSession();
        session.Start(Info(10), PartPath);
        var items = new[] { Item(0), Item(100), Item(600), Item(700), Item(800) };

        var waveform = session.BuildWaveform(items);

        Assert.NotNull(waveform);
        Assert.Equal(20, waveform!.DanmakuDensity.Length);
        Assert.Equal(2f / 3f, waveform.DanmakuDensity[0], 5);
        Assert.Equal(1f, waveform.DanmakuDensity[1], 5);
        Assert.Equal(3, waveform.MaxDanmakuPerBucket);
    }

    [Fact]
    public void BuildWaveform_IgnoresDanmakuOutsideTimeline()
    {
        var session = new LiveClipSession();
        session.Start(Info(10), PartPath);
        var items = new[] { Item(60_000), Item(-600) };

        var waveform = session.BuildWaveform(items);

        Assert.NotNull(waveform);
        Assert.Equal(0, waveform!.MaxDanmakuPerBucket);
        Assert.All(waveform.DanmakuDensity, density => Assert.Equal(0f, density));
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);
        session.AdviseDanmakuOffset(100);
        session.AppendPeaks(new[] { 0.5f }, 1);
        session.MarkPeaksExtracted(TimeSpan.FromSeconds(10));

        session.Reset();

        Assert.False(session.IsActive);
        Assert.Null(session.Info);
        Assert.Null(session.PartPath);
        Assert.Equal(0, session.DanmakuOffset);
        Assert.Equal(TimeSpan.Zero, session.RecordedDuration);
        Assert.Equal(TimeSpan.Zero, session.SafeClipDuration);
        Assert.Equal(string.Empty, session.StatusText);
        Assert.Equal(TimeSpan.Zero, session.PeaksExtractedUntil);
        Assert.Null(session.BuildWaveform(null));
    }

    [Fact]
    public void Reset_ThenApplyLatest_StartsNewSessionFromScratch()
    {
        var session = new LiveClipSession();
        session.Start(Info(65), PartPath);
        session.Reset();
        var newInfo = Info(70, partNumber: 1);

        session.ApplyLatest(newInfo, PartPath);

        Assert.True(session.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(70), session.RecordedDuration);
        Assert.NotNull(session.BuildWaveform(null));
    }

    [Fact]
    public void Start_WithPartNumberAboveOne_IsStillAcceptedByModel()
    {
        // 分片号校验仍由 ViewModel 编排层负责（PartNumber>1 时不进入 live 模式）；
        // 模型本身只承载状态，不做业务拒绝。
        var session = new LiveClipSession();

        session.Start(Info(65, partNumber: 2), PartPath);

        Assert.True(session.IsActive);
        Assert.Equal(2, session.Info!.PartNumber);
    }
}
