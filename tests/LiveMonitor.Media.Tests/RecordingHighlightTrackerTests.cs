using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class RecordingHighlightTrackerTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enrich_WithoutRegistration_ReturnsEventUnchanged()
    {
        var tracker = new RecordingHighlightTracker();
        var highlightEvent = CreateEvent("1001", Anchor.AddSeconds(3));

        var enriched = tracker.Enrich(highlightEvent);

        Assert.Null(enriched.RecordingSessionId);
        Assert.Null(enriched.RecordingOffsetMs);
    }

    [Fact]
    public void Enrich_AfterRegister_BindsSessionId_AndOffsetWhenAnchorKnown()
    {
        var tracker = new RecordingHighlightTracker();
        var sessionId = Guid.NewGuid();
        tracker.Register(PlatformType.Bilibili, "1001", sessionId);

        var withoutAnchor = tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(3)));
        Assert.Equal(sessionId, withoutAnchor.RecordingSessionId);
        Assert.Null(withoutAnchor.RecordingOffsetMs);

        tracker.UpdateAnchor(sessionId, Anchor);
        var withAnchor = tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(3.5)));

        Assert.Equal(sessionId, withAnchor.RecordingSessionId);
        Assert.Equal(3500, withAnchor.RecordingOffsetMs);
    }

    [Fact]
    public void Enrich_NegativeOffset_IsClampedToZero()
    {
        var tracker = new RecordingHighlightTracker();
        var sessionId = Guid.NewGuid();
        tracker.Register(PlatformType.Bilibili, "1001", sessionId);
        tracker.UpdateAnchor(sessionId, Anchor);

        var enriched = tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(-2)));

        Assert.Equal(0, enriched.RecordingOffsetMs);
    }

    [Fact]
    public void Enrich_MatchesRoomIdIgnoringCaseAndWhitespace()
    {
        var tracker = new RecordingHighlightTracker();
        var sessionId = Guid.NewGuid();
        tracker.Register(PlatformType.Bilibili, "  ABC  ", sessionId);
        tracker.UpdateAnchor(sessionId, Anchor);

        var enriched = tracker.Enrich(CreateEvent("abc", Anchor.AddSeconds(1)));

        Assert.Equal(sessionId, enriched.RecordingSessionId);
    }

    [Fact]
    public void Enrich_TracksHighlightCount_AndRemoveReturnsIt()
    {
        var tracker = new RecordingHighlightTracker();
        var sessionId = Guid.NewGuid();
        tracker.Register(PlatformType.Bilibili, "1001", sessionId);
        tracker.UpdateAnchor(sessionId, Anchor);

        tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(1)));
        tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(2)));
        tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(3)));

        Assert.Equal(3, tracker.GetHighlightCount(sessionId));
        Assert.Equal(3, tracker.Remove(PlatformType.Bilibili, "1001"));
        Assert.Equal(0, tracker.GetHighlightCount(sessionId));
        Assert.Null(tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(4))).RecordingSessionId);
    }

    [Fact]
    public void Remove_UnknownRoom_ReturnsZero()
    {
        var tracker = new RecordingHighlightTracker();

        Assert.Equal(0, tracker.Remove(PlatformType.Bilibili, "missing"));
    }

    [Fact]
    public void UpdateAnchor_OnlyFirstOneTakesEffect()
    {
        var tracker = new RecordingHighlightTracker();
        var sessionId = Guid.NewGuid();
        tracker.Register(PlatformType.Bilibili, "1001", sessionId);

        tracker.UpdateAnchor(sessionId, Anchor);
        tracker.UpdateAnchor(sessionId, Anchor.AddSeconds(100));

        var enriched = tracker.Enrich(CreateEvent("1001", Anchor.AddSeconds(2)));

        Assert.Equal(2000, enriched.RecordingOffsetMs);
    }

    [Fact]
    public void Register_SameRoomTwice_KeepsSingleBinding()
    {
        var tracker = new RecordingHighlightTracker();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        tracker.Register(PlatformType.Bilibili, "1001", first);
        tracker.Register(PlatformType.Bilibili, "1001", second);

        Assert.Equal(1, tracker.Count);
        Assert.Equal(second, tracker.Enrich(CreateEvent("1001", Anchor)).RecordingSessionId);
    }

    [Fact]
    public void Register_RejectsEmptySessionOrRoom()
    {
        var tracker = new RecordingHighlightTracker();

        Assert.Throws<ArgumentException>(() => tracker.Register(PlatformType.Bilibili, "1001", Guid.Empty));
        Assert.Throws<ArgumentException>(() => tracker.Register(PlatformType.Bilibili, "  ", Guid.NewGuid()));
    }

    private static HighlightEvent CreateEvent(string roomId, DateTimeOffset triggerTime)
        => new(
            PlatformType.Bilibili,
            roomId,
            triggerTime,
            CurrentRatePerSec: 12,
            BaselineRatePerSec: 1,
            TriggerReason: "密度突增（测试）");
}
