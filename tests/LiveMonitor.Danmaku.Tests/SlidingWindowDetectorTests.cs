using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku.Analyzers;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

public class SlidingWindowDetectorTests
{
    private static (SlidingWindowDetector Detector, List<HighlightEvent> Events) CreateDetector(
        FakeClock clock,
        HighlightRuleOptions? options = null)
    {
        var detector = new SlidingWindowDetector(options ?? HighlightRuleOptions.Default, clock.Reader);
        var events = new List<HighlightEvent>();
        detector.HighlightTriggered += events.Add;
        return (detector, events);
    }

    private static void SendBurst(
        SlidingWindowDetector detector,
        int count,
        string content = "普通弹幕",
        string roomId = TestDanmaku.DefaultRoomId)
    {
        for (var i = 0; i < count; i++)
        {
            detector.Ingest(TestDanmaku.Create(content, roomId: roomId));
        }
    }

    [Fact]
    public void Ingest_BelowThreshold_DoesNotTrigger()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 14);

        Assert.Empty(events);
    }

    [Fact]
    public void Ingest_ReachingThreshold_TriggersWithRatesAndReason()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15);

        var highlight = Assert.Single(events);
        Assert.Equal(PlatformType.Bilibili, highlight.Platform);
        Assert.Equal(TestDanmaku.DefaultRoomId, highlight.RoomId);
        Assert.Equal(clock.Now, highlight.TriggerTime);
        Assert.Equal(1.5, highlight.CurrentRatePerSec, 6);
        Assert.Equal(15.0 / 180.0, highlight.BaselineRatePerSec, 6);
        Assert.Contains("15 条", highlight.TriggerReason);
        Assert.Contains("10 秒", highlight.TriggerReason);
        Assert.Contains("180 秒", highlight.TriggerReason);
    }

    [Fact]
    public void Cooldown_PreventsDuplicateTriggers_UntilExpired()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15);
        Assert.Single(events);

        SendBurst(detector, 15);
        Assert.Single(events);

        clock.Advance(TimeSpan.FromSeconds(61));
        SendBurst(detector, 15);

        Assert.Equal(2, events.Count);
        Assert.Equal(1.5, events[1].CurrentRatePerSec, 6);
        Assert.Equal(45.0 / 180.0, events[1].BaselineRatePerSec, 6);
    }

    [Fact]
    public void ColdRoom_FewMessages_DoesNotTrigger()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 5);

        Assert.Empty(events);
    }

    [Fact]
    public void BoostKeywords_IncreaseShortWindowWeight()
    {
        var clock = new FakeClock();
        var (boosted, boostedEvents) = CreateDetector(clock);
        var (plain, plainEvents) = CreateDetector(
            clock,
            HighlightRuleOptions.Default with { BoostKeywords = Array.Empty<string>() });

        // 冷清基线：150 秒每秒 1 条普通弹幕。
        for (var second = 0; second < 150; second++)
        {
            boosted.Ingest(TestDanmaku.Create("普通弹幕"));
            plain.Ingest(TestDanmaku.Create("普通弹幕"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // 3 秒共 15 条 "666太强了"：含 Boost 关键词，每条权重 1.5。
        for (var round = 0; round < 3; round++)
        {
            SendBurst(boosted, 5, "666太强了");
            SendBurst(plain, 5, "666太强了");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Empty(plainEvents);
        var highlight = Assert.Single(boostedEvents);
        Assert.Equal(2.95, highlight.CurrentRatePerSec, 6);
        Assert.Equal(172.5 / 180.0, highlight.BaselineRatePerSec, 6);
    }

    [Fact]
    public void BoostKeyword_AddsWeightAtMostOncePerMessage()
    {
        var clock = new FakeClock();
        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = 10,
            BaselineWindowSeconds = 10,
            DensityMultiplier = 0.5,
            MinShortWindowCount = 1,
            CooldownSeconds = 0,
            BoostKeywords = new[] { "666" },
        };
        var (detector, events) = CreateDetector(clock, options);

        detector.Ingest(TestDanmaku.Create("666，666！"));

        var highlight = Assert.Single(events);
        Assert.Equal(0.15, highlight.CurrentRatePerSec, 6);
        Assert.Equal(0.15, highlight.BaselineRatePerSec, 6);
    }

    [Fact]
    public void BoostKeyword_MatchesIgnoreCase()
    {
        var clock = new FakeClock();
        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = 10,
            BaselineWindowSeconds = 10,
            DensityMultiplier = 0.5,
            MinShortWindowCount = 1,
            CooldownSeconds = 0,
            BoostKeywords = new[] { "Pog" },
        };
        var (detector, events) = CreateDetector(clock, options);

        detector.Ingest(TestDanmaku.Create("POG 太强了"));

        var highlight = Assert.Single(events);
        Assert.Equal(0.15, highlight.CurrentRatePerSec, 6);
    }

    [Fact]
    public void Rooms_AreIsolated()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15, roomId: "room-a");
        SendBurst(detector, 15, roomId: "room-b");
        SendBurst(detector, 15, roomId: "room-a");

        Assert.Equal(2, events.Count);
        Assert.Equal("room-a", events[0].RoomId);
        Assert.Equal("room-b", events[1].RoomId);
    }

    [Fact]
    public void UpdateRules_HotUpdatesThresholds()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 6);
        Assert.Empty(events);

        detector.UpdateRules(HighlightRuleOptions.Default with
        {
            MinShortWindowCount = 5,
            DensityMultiplier = 0.1,
            CooldownSeconds = 0,
        });
        SendBurst(detector, 1);

        Assert.Single(events);
    }

    [Fact]
    public void UpdateRules_WindowChange_ClearsWindowAndCooldownState()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15);
        Assert.Single(events);

        detector.UpdateRules(HighlightRuleOptions.Default with { BaselineWindowSeconds = 120 });
        SendBurst(detector, 15);

        Assert.Equal(2, events.Count);
        Assert.Equal(120, detector.CurrentRules.BaselineWindowSeconds);
    }

    [Fact]
    public void UpdateRules_WindowUnchanged_KeepsBaselineData()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15);
        Assert.Single(events);

        clock.Advance(TimeSpan.FromSeconds(61));
        detector.UpdateRules(HighlightRuleOptions.Default with { DensityMultiplier = 2.0 });
        SendBurst(detector, 15);

        Assert.Equal(2, events.Count);
        Assert.Equal(30.0 / 180.0, events[1].BaselineRatePerSec, 6);
    }

    [Fact]
    public void OldBuckets_AgeOut_AfterBaselineWindow()
    {
        var clock = new FakeClock();
        var options = HighlightRuleOptions.Default with
        {
            DensityMultiplier = 5.0,
            MinShortWindowCount = 15,
            CooldownSeconds = 0,
        };
        var (detector, events) = CreateDetector(clock, options);

        // 100 秒稀疏弹幕，随后静默 180 秒，旧数据应滑出 180 秒基线窗口。
        for (var second = 0; second < 100; second++)
        {
            detector.Ingest(TestDanmaku.Create("普通弹幕"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        clock.Advance(TimeSpan.FromSeconds(180));

        for (var round = 0; round < 3; round++)
        {
            SendBurst(detector, 5);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var highlight = Assert.Single(events);
        Assert.Equal(15.0 / 180.0, highlight.BaselineRatePerSec, 6);
    }

    [Fact]
    public void Reset_ClearsAllRooms()
    {
        var clock = new FakeClock();
        var (detector, events) = CreateDetector(clock);

        SendBurst(detector, 15);
        Assert.Single(events);

        detector.Reset();
        SendBurst(detector, 15);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ConstructorAndUpdateRules_NormalizeInvalidOptions()
    {
        var clock = new FakeClock();
        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = 0,
            BaselineWindowSeconds = 0,
            DensityMultiplier = double.NaN,
            MinShortWindowCount = 0,
            CooldownSeconds = -5,
        };
        var (detector, _) = CreateDetector(clock, options);

        Assert.Equal(1, detector.CurrentRules.ShortWindowSeconds);
        Assert.Equal(1, detector.CurrentRules.BaselineWindowSeconds);
        Assert.Equal(3.0, detector.CurrentRules.DensityMultiplier, 6);
        Assert.Equal(1, detector.CurrentRules.MinShortWindowCount);
        Assert.Equal(0, detector.CurrentRules.CooldownSeconds);

        detector.UpdateRules(HighlightRuleOptions.Default with
        {
            ShortWindowSeconds = 0,
            BaselineWindowSeconds = 999_999,
        });

        Assert.Equal(1, detector.CurrentRules.ShortWindowSeconds);
        Assert.Equal(3600, detector.CurrentRules.BaselineWindowSeconds);
    }
}
