using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class LiveWaveformLogicTests
{
    [Theory]
    [InlineData(2.0, 0.0, 2.0, 4)]
    [InlineData(3.2, 0.0, 3.0, 6)]
    [InlineData(10.75, 4.0, 10.5, 13)]
    public void TryComputeExtractionWindow_AlignsEndToBucketGrid(
        double safeSeconds,
        double fromSeconds,
        double expectedToSeconds,
        int expectedCount)
    {
        var safe = TimeSpan.FromSeconds(safeSeconds);
        var from = TimeSpan.FromSeconds(fromSeconds);

        var hasWork = LiveWaveformLogic.TryComputeExtractionWindow(safe, from, out var window);

        Assert.True(hasWork);
        Assert.Equal(TimeSpan.FromSeconds(expectedToSeconds), window.To);
        Assert.Equal(expectedCount, window.ExpectedCount);
    }

    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(3.2, 3.5)]
    [InlineData(0.4, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-5.0, 0.0)]
    public void TryComputeExtractionWindow_ReturnsFalseWhenNoNewBuckets(double safeSeconds, double fromSeconds)
    {
        var hasWork = LiveWaveformLogic.TryComputeExtractionWindow(
            TimeSpan.FromSeconds(safeSeconds),
            TimeSpan.FromSeconds(fromSeconds),
            out _);

        Assert.False(hasWork);
    }

    [Fact]
    public void AppendPeaks_NoExisting_PadsExpectedCount()
    {
        var merged = LiveWaveformLogic.AppendPeaks(null, new[] { 0.1f, 0.5f }, 4);

        Assert.Equal(new[] { 0.1f, 0.5f, 0f, 0f }, merged);
    }

    [Fact]
    public void AppendPeaks_Existing_CopiesThenPads()
    {
        var merged = LiveWaveformLogic.AppendPeaks(new[] { 0.1f, 0.2f }, new[] { 0.9f }, 3);

        Assert.Equal(new[] { 0.1f, 0.2f, 0.9f, 0f, 0f }, merged);
    }

    [Fact]
    public void AppendPeaks_TruncatesExtraSamples()
    {
        var merged = LiveWaveformLogic.AppendPeaks(null, new[] { 0.1f, 0.2f, 0.3f }, 2);

        Assert.Equal(new[] { 0.1f, 0.2f }, merged);
    }

    [Fact]
    public void AppendPeaks_EmptySamples_AppendsZeros()
    {
        var merged = LiveWaveformLogic.AppendPeaks(new[] { 0.4f }, Array.Empty<float>(), 2);

        Assert.Equal(new[] { 0.4f, 0f, 0f }, merged);
    }

    [Fact]
    public void AppendPeaks_NonPositiveCount_ReturnsExistingArray()
    {
        var existing = new[] { 0.1f, 0.2f };

        Assert.Same(existing, LiveWaveformLogic.AppendPeaks(existing, Array.Empty<float>(), 0));
        Assert.Same(existing, LiveWaveformLogic.AppendPeaks(existing, Array.Empty<float>(), -3));
    }

    [Fact]
    public void AppendPeaks_NullExistingAndNonPositiveCount_ReturnsEmpty()
    {
        var merged = LiveWaveformLogic.AppendPeaks(null, Array.Empty<float>(), 0);

        Assert.Empty(merged);
    }

    [Fact]
    public void Build_CreatesBucketsForDurationAndCopiesPeaks()
    {
        var data = LiveWaveformLogic.Build(
            TimeSpan.FromSeconds(2),
            new[] { 0.1f, 0.2f },
            null);

        Assert.Equal(TimeSpan.FromSeconds(2), data.Duration);
        Assert.Equal(4, data.AudioPeaks.Length);
        Assert.Equal(new[] { 0.1f, 0.2f, 0f, 0f }, data.AudioPeaks);
        Assert.Equal(new[] { 0f, 0f, 0f, 0f }, data.DanmakuDensity);
        Assert.Equal(0, data.MaxDanmakuPerBucket);
    }

    [Fact]
    public void Build_RoundsBucketCountUp()
    {
        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(1.2), null, null);

        Assert.Equal(3, data.AudioPeaks.Length);
    }

    [Fact]
    public void Build_TruncatesPeaksLongerThanBuckets()
    {
        var data = LiveWaveformLogic.Build(
            TimeSpan.FromSeconds(2),
            new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f },
            null);

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, data.AudioPeaks);
    }

    [Fact]
    public void Build_NullPeaks_ReturnsZeroAudio()
    {
        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(1), null, null);

        Assert.Equal(new[] { 0f, 0f }, data.AudioPeaks);
    }

    [Fact]
    public void Build_DanmakuDensity_NormalizesByMaxBucket()
    {
        var items = new[]
        {
            CreateItem(0),
            CreateItem(100),
            CreateItem(600),
            CreateItem(1200),
            CreateItem(5000)
        };

        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(2), null, items);

        Assert.Equal(new[] { 1f, 0.5f, 0.5f, 0f }, data.DanmakuDensity);
        Assert.Equal(2, data.MaxDanmakuPerBucket);
    }

    [Fact]
    public void Build_NegativeOffsets_TruncateIntoFirstBucketLikePreviousBehavior()
    {
        // 原实现用 (int)(OffsetMs / 500.0) 截断：-600ms 落到 -1 被丢弃，
        // -10ms 截断为 0 仍计入第 0 桶（保持重构前行为）。
        var items = new[] { CreateItem(-10), CreateItem(-600) };

        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(1), null, items);

        Assert.Equal(new[] { 1f, 0f }, data.DanmakuDensity);
        Assert.Equal(1, data.MaxDanmakuPerBucket);
    }

    [Fact]
    public void Build_EmptyDanmaku_ReturnsZeroDensity()
    {
        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(1), null, Array.Empty<RecordedDanmakuItem>());

        Assert.Equal(new[] { 0f, 0f }, data.DanmakuDensity);
        Assert.Equal(0, data.MaxDanmakuPerBucket);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.2, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0.51, 2)]
    public void Build_NonPositiveDuration_KeepsAtLeastOneBucket(double seconds, int expectedBuckets)
    {
        var data = LiveWaveformLogic.Build(TimeSpan.FromSeconds(seconds), null, null);

        Assert.Equal(expectedBuckets, data.AudioPeaks.Length);
    }

    [Fact]
    public void Constants_MatchRealtimeTimelineGrid()
    {
        Assert.Equal(0.5, LiveWaveformLogic.PeakBucketSeconds);
        Assert.Equal(2, LiveWaveformLogic.PeaksPerSecond);
    }

    private static RecordedDanmakuItem CreateItem(long offsetMs) =>
        new(offsetMs, "sender", "text", IsSticker: false, IsGift: false);
}
