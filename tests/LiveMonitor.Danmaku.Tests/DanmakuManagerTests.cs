using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

/// <summary>
/// DanmakuManager 集成测试：用 Fake 客户端/仓储 + 注入 delay/clock/batchInterval 验证
/// 连接生命周期、批处理管线、过滤器、高能检测、热更新与重连退避。
/// </summary>
public class DanmakuManagerTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Acquire_BeforeInitialize_ThrowsInvalidOperationException()
    {
        await using var harness = new DanmakuManagerHarness();

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Manager.Acquire(PlatformType.Bilibili, "1001"));

        Assert.Equal("请先 InitializeAsync", exception.Message);
    }

    [Fact]
    public async Task Acquire_SameRoomTwice_SharesOneConnection_AndDisconnectsOnlyAtZero()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle1 = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var handle2 = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var client = await WaitForSessionClientAsync(harness);

        await TestWait.WaitUntilAsync(() => client.ConnectCount == 1, message: "同 key 只连接一次");
        await Task.Delay(100);
        Assert.Equal(1, harness.Factory.CreateCount);
        Assert.Equal(1, client.ConnectCount);

        handle1.Dispose();
        await Task.Delay(100);
        Assert.Equal(0, client.DisconnectCount);

        handle2.Dispose();
        await TestWait.WaitUntilAsync(() => client.DisconnectCount >= 1, message: "引用归零后断开");
        Assert.Equal(1, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task Acquire_RoomKey_IgnoresCaseAndWhitespace()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle1 = harness.Manager.Acquire(PlatformType.Bilibili, "ROOM42");
        var handle2 = harness.Manager.Acquire(PlatformType.Bilibili, " room42 ");

        await TestWait.WaitUntilAsync(() => harness.Factory.CreateCount == 1, message: "大小写/空白归一化后共享会话");
        await Task.Delay(50);
        Assert.Equal(1, harness.Factory.CreateCount);
        Assert.Equal("ROOM42", handle1.RoomId);
        Assert.Equal("room42", handle2.RoomId);
        Assert.Equal(PlatformType.Bilibili, handle1.Platform);

        handle1.Dispose();
        handle2.Dispose();
    }

    [Fact]
    public async Task Acquire_DifferentRoomsAndPlatforms_CreateIndependentConnections()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle1 = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var handle2 = harness.Manager.Acquire(PlatformType.Bilibili, "1002");
        var handle3 = harness.Manager.Acquire(PlatformType.Twitch, "1001");

        await harness.WaitForCreatedClientsAsync(3);
        await TestWait.WaitUntilAsync(
            () => harness.Factory.Clients.All(client => client.ConnectCount > 0),
            message: "三个会话全部连接");

        var keys = harness.Factory.Clients
            .Select(client => (client.Platform, client.RoomId))
            .ToHashSet();
        Assert.Equal(3, keys.Count);
        Assert.Contains((PlatformType.Bilibili, "1001"), keys);
        Assert.Contains((PlatformType.Bilibili, "1002"), keys);
        Assert.Contains((PlatformType.Twitch, "1001"), keys);

        handle1.Dispose();
        handle2.Dispose();
        handle3.Dispose();
    }

    [Fact]
    public async Task Acquire_PassesCookieFromCredentialStore()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Credentials.SetCookie(PlatformType.Bilibili, "SESSDATA=token");
        await harness.InitializeAsync();

        harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var client = await WaitForSessionClientAsync(harness);

        Assert.Equal("SESSDATA=token", harness.Factory.LastCookie);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task CredentialStoreFailure_ConnectsWithoutCookie()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Credentials.ThrowOnGetCookie = true;
        await harness.InitializeAsync();

        harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        await WaitForSessionClientAsync(harness);

        Assert.Null(harness.Factory.LastCookie);
        Assert.Contains(harness.Logs, message => message.Contains("Cookie"));
    }

    [Fact]
    public async Task MessagesReceived_DeliversBatchedMessagesToHandle()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 3);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("第一条"));
        client.RaiseMessage(TestDanmaku.Create("第二条"));
        client.RaiseMessage(TestDanmaku.Create("第三条"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(new[] { "第一条", "第二条", "第三条" }, received.Select(message => message.Content));
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 3, message: "句柄消息计数");
    }

    [Fact]
    public async Task KeywordBlockedMessages_NeverReachHandle()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.Blocklist.AddAsync(new DanmakuBlockKeyword { Keyword = "spam" });
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 2);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("SPAM everywhere"));
        client.RaiseMessage(TestDanmaku.Create("hello"));
        client.RaiseMessage(TestDanmaku.Create("world"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(new[] { "hello", "world" }, received.Select(message => message.Content));
        await Task.Delay(100);
        Assert.Equal(2, handle.MessageCount);
        Assert.Equal(0, handle.BlockedCount);
        Assert.Equal(0, harness.Manager.UnicycleBlockedCount);
    }

    [Fact]
    public async Task UnicycleBlocked_IncrementsHandleAndGlobalCounts_AndRaisesEvent()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Settings.Seed(AppSettingKeys.BlockUnicycleEnabled, "true");
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 2);
        var globalCountTask = NextUnicycleCountAsync(harness.Manager, atLeast: 2);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("第一条超长弹幕内容AAAAAAAAAAAA", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第二条超长弹幕内容BBBBBBBBBBBB", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第三条超长弹幕内容CCCCCCCCCCCC", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第四条超长弹幕内容DDDDDDDDDDDD", uid: "9999"));

        var globalCount = await globalCountTask.WaitAsync(ShortWait);
        Assert.Equal(2, globalCount);
        Assert.Equal(2, harness.Manager.UnicycleBlockedCount);

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(2, received.Count);
        Assert.Equal(2, handle.BlockedCount);
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 2, message: "拦截消息不进入渲染批次");
        await Task.Delay(100);
        Assert.Equal(2, handle.MessageCount);
    }

    [Fact]
    public async Task UnicycleDisabledByDefault_LongTextBurstPasses()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 3);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("第一条超长弹幕内容AAAAAAAAAAAA", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第二条超长弹幕内容BBBBBBBBBBBB", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第三条超长弹幕内容CCCCCCCCCCCC", uid: "9999"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(3, received.Count);
        Assert.Equal(0, handle.BlockedCount);
        Assert.Equal(0, harness.Manager.UnicycleBlockedCount);
    }

    [Fact]
    public async Task SetUnicycleEnabledAsync_PersistsSetting_AndUpdatesExistingSessions()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 3);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("第一条超长弹幕内容AAAAAAAAAAAA", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第二条超长弹幕内容BBBBBBBBBBBB", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第三条超长弹幕内容CCCCCCCCCCCC", uid: "9999"));
        await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(0, handle.BlockedCount);

        Assert.True(await harness.Manager.SetUnicycleEnabledAsync(true));
        Assert.Equal("true", harness.Settings.Snapshot[AppSettingKeys.BlockUnicycleEnabled]);

        client.RaiseMessage(TestDanmaku.Create("第五条超长弹幕内容EEEEEEEEEEEE", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第六条超长弹幕内容FFFFFFFFFFFF", uid: "9999"));
        client.RaiseMessage(TestDanmaku.Create("第七条超长弹幕内容GGGGGGGGGGGG", uid: "9999"));

        await TestWait.WaitUntilAsync(() => handle.BlockedCount == 1, message: "启用后热更新过滤器");
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 5, message: "启用后放行前两条长文");
        Assert.Equal(1, harness.Manager.UnicycleBlockedCount);

        Assert.True(await harness.Manager.SetUnicycleEnabledAsync(false));
        Assert.Equal("false", harness.Settings.Snapshot[AppSettingKeys.BlockUnicycleEnabled]);
        client.RaiseMessage(TestDanmaku.Create("第八条超长弹幕内容HHHHHHHHHHHH", uid: "9999"));
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 6, message: "关闭后全部放行");
        Assert.Equal(1, handle.BlockedCount);
    }

    [Fact]
    public async Task ReloadSettingsAsync_HotUpdatesKeywordFilter()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 2);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("hello"));
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 1, message: "首条消息投递");

        await harness.Blocklist.AddAsync(new DanmakuBlockKeyword { Keyword = "bad" });
        await harness.Manager.ReloadSettingsAsync();

        client.RaiseMessage(TestDanmaku.Create("bad content"));
        client.RaiseMessage(TestDanmaku.Create("good content"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(new[] { "hello", "good content" }, received.Select(message => message.Content));
        await Task.Delay(100);
        Assert.Equal(2, handle.MessageCount);
    }

    [Fact]
    public async Task InitializeAsync_CanBeCalledRepeatedly_AndHotUpdatesKeywordFilter()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var messagesTask = CollectBatchesAsync(handle, 2);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("hello"));
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 1, message: "首条消息投递");

        await harness.Blocklist.AddAsync(new DanmakuBlockKeyword { Keyword = "bad" });
        await harness.InitializeAsync(); // 幂等重入：重读设置并热更新已有会话
        await harness.InitializeAsync();

        client.RaiseMessage(TestDanmaku.Create("bad content"));
        client.RaiseMessage(TestDanmaku.Create("other content"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(new[] { "hello", "other content" }, received.Select(message => message.Content));
        await Task.Delay(100);
        Assert.Equal(2, handle.MessageCount);
        Assert.Equal(1, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task HighlightTriggered_RaisesManagerEvent_AndPersistsEvent()
    {
        await using var harness = new DanmakuManagerHarness();
        SeedSensitiveHighlightSettings(harness);
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var highlightTask = NextHighlightAsync(harness.Manager);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("666"));

        var highlight = await highlightTask.WaitAsync(ShortWait);
        Assert.Equal(PlatformType.Bilibili, highlight.Platform);
        Assert.Equal("1001", highlight.RoomId);
        Assert.Contains("密度", highlight.TriggerReason);
        Assert.True(highlight.CurrentRatePerSec > 0);

        await TestWait.WaitUntilAsync(
            () => harness.Highlights.Snapshot.Count == 1,
            message: "高能事件异步落盘");
        Assert.Equal(highlight.Id, harness.Highlights.Snapshot[0].Id);
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 1, message: "高能消息仍进入渲染批次");
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingHighlightEvents_AndIsIdempotent()
    {
        await using var harness = new DanmakuManagerHarness();
        SeedSensitiveHighlightSettings(harness);
        await harness.InitializeAsync();

        harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var highlightTask = NextHighlightAsync(harness.Manager);
        var client = await WaitForSessionClientAsync(harness);
        client.RaiseMessage(TestDanmaku.Create("666"));
        await highlightTask.WaitAsync(ShortWait);

        await harness.Manager.DisposeAsync();
        Assert.Single(harness.Highlights.Snapshot);

        await harness.Manager.DisposeAsync();
        Assert.Single(harness.Highlights.Snapshot);
    }

    [Fact]
    public async Task UpdateHighlightRulesAsync_PersistsSettings_AndUpdatesDetector()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync(); // 默认 MinShortWindowCount=15，单条不触发

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var triggerCount = 0;
        harness.Manager.HighlightTriggered += _ => Interlocked.Increment(ref triggerCount);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("hello"));
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref triggerCount));

        var rules = new HighlightRuleOptions
        {
            MinShortWindowCount = 1,
            DensityMultiplier = 0.1,
            CooldownSeconds = 0,
        };
        await harness.Manager.UpdateHighlightRulesAsync(rules);

        Assert.Equal("1", harness.Settings.Snapshot[AppSettingKeys.HighlightMinShortWindowCount]);
        Assert.Equal("0.1", harness.Settings.Snapshot[AppSettingKeys.HighlightDensityMultiplier]);
        Assert.Equal("0", harness.Settings.Snapshot[AppSettingKeys.HighlightCooldownSeconds]);

        var highlightTask = NextHighlightAsync(harness.Manager);
        client.RaiseMessage(TestDanmaku.Create("666"));
        var highlight = await highlightTask.WaitAsync(ShortWait);
        Assert.Equal("1001", highlight.RoomId);

        await TestWait.WaitUntilAsync(() => harness.Highlights.Snapshot.Count == 1, message: "热更新后的高能事件落盘");
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 2, message: "消息管线不受高能检测影响");
    }

    [Fact]
    public async Task ConnectionFailure_UsesBackoffSequence_3s10s30s60s()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Factory.ConnectFailureOnAttempt = _ => new IOException("connect failed");
        await harness.InitializeAsync();

        using var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");

        await TestWait.WaitUntilAsync(
            () => harness.Delay.BackoffWaits.Count >= 4,
            message: "退避重试序列");

        var backoffs = harness.Delay.BackoffWaits.Take(4).ToArray();
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
            },
            backoffs);
        Assert.True(harness.Factory.CreateCount >= 5);
    }

    [Fact]
    public async Task StableConnection_Over30Seconds_ResetsBackoffTo3Seconds()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Factory.ConnectFailureOnAttempt = index => index == 0 ? new IOException("first failure") : null;
        await harness.InitializeAsync();

        using var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");

        // 第 0 次连接失败 → 退避 3 秒；第 1 次连接成功。
        var firstClient = await harness.WaitForClientAsync(index: 1);
        await TestWait.WaitUntilAsync(() => harness.Delay.BackoffWaits.Count >= 1, message: "首次失败退避");
        Assert.Equal(TimeSpan.FromSeconds(3), harness.Delay.BackoffWaits[0]);

        // 稳定连接超过 30 秒后断开，退避应重置回 3 秒（而不是继续 10 秒）。
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        firstClient.RaiseDisconnected(new IOException("server closed"));

        await TestWait.WaitUntilAsync(() => harness.Delay.BackoffWaits.Count >= 2, message: "断线重连退避");
        Assert.Equal(TimeSpan.FromSeconds(3), harness.Delay.BackoffWaits[1]);

        await harness.WaitForClientAsync(index: 2);
        Assert.True(harness.Factory.CreateCount >= 3);
    }

    [Fact]
    public async Task BackoffWait_IsCancelledPromptly_WhenLastHandleReleased()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Factory.ConnectFailureOnAttempt = _ => new IOException("connect failed");
        harness.Delay.BlockBackoff = true;
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        await TestWait.WaitUntilAsync(() => harness.Delay.BackoffWaits.Count >= 1, message: "进入退避等待");

        handle.Dispose();

        await TestWait.WaitUntilAsync(
            () => harness.Logs.Any(message => message.Contains("连接循环已退出")),
            message: "释放引用后退避等待被取消、后台循环退出");
        Assert.Equal(1, harness.Factory.CreateCount);
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsAllSessions_AndIsIdempotent()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var handle1 = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var handle2 = harness.Manager.Acquire(PlatformType.Bilibili, "1002");
        var handle3 = harness.Manager.Acquire(PlatformType.Twitch, "1001");
        await harness.WaitForCreatedClientsAsync(3);
        await TestWait.WaitUntilAsync(
            () => harness.Factory.Clients.All(client => client.ConnectCount > 0),
            message: "全部会话连接");

        await harness.Manager.DisposeAsync();

        Assert.All(harness.Factory.Clients, client => Assert.True(client.DisconnectCount >= 1));
        await harness.Manager.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => harness.Manager.Acquire(PlatformType.Bilibili, "1001"));

        handle1.Dispose();
        handle2.Dispose();
        handle3.Dispose();
    }

    [Fact]
    public async Task FactoryReturnsNull_LogsAndDoesNotRetry()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Factory.ReturnNull = true;
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Douyin, "1001");

        await TestWait.WaitUntilAsync(() => harness.Factory.CreateCount >= 1, message: "工厂被调用一次");
        await TestWait.WaitUntilAsync(
            () => harness.Logs.Any(message => message.Contains("不支持")),
            message: "记录不支持平台日志");

        await Task.Delay(100);
        Assert.Equal(1, harness.Factory.CreateCount);
        Assert.Equal(0, handle.MessageCount);
        Assert.Empty(harness.Delay.BackoffWaits);
    }

    [Fact]
    public async Task GetRecentHighlightEventsAsync_DelegatesToRepository()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var older = new HighlightEvent(
            PlatformType.Bilibili,
            "1001",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            1.0,
            0.1,
            "old");
        var newer = new HighlightEvent(
            PlatformType.Bilibili,
            "1001",
            new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero),
            2.0,
            0.2,
            "new");
        await harness.Highlights.AddAsync(older);
        await harness.Highlights.AddAsync(newer);

        var recent = await harness.Manager.GetRecentHighlightEventsAsync(1);

        Assert.Single(recent);
        Assert.Equal(newer.Id, recent[0].Id);
    }

    [Fact]
    public async Task HandleDispose_StopsMessageDelivery()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();

        var kept = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var released = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var keptMessagesTask = CollectBatchesAsync(kept, 1);
        var client = await WaitForSessionClientAsync(harness);

        released.Dispose();
        await Task.Delay(50);
        client.RaiseMessage(TestDanmaku.Create("after dispose"));

        var received = await keptMessagesTask.WaitAsync(ShortWait);
        Assert.Single(received);
        await TestWait.WaitUntilAsync(() => kept.MessageCount == 1, message: "保留句柄仍收到消息");
        Assert.Equal(0, released.MessageCount);
    }

    [Fact]
    public async Task HighlightPersistenceFailure_DoesNotBreakMessagePipeline()
    {
        await using var harness = new DanmakuManagerHarness();
        SeedSensitiveHighlightSettings(harness);
        harness.Highlights.ThrowOnAdd = true;
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var highlightTask = NextHighlightAsync(harness.Manager);
        var messagesTask = CollectBatchesAsync(handle, 2);
        var client = await WaitForSessionClientAsync(harness);

        client.RaiseMessage(TestDanmaku.Create("666"));
        await highlightTask.WaitAsync(ShortWait);
        client.RaiseMessage(TestDanmaku.Create("hello"));

        var received = await messagesTask.WaitAsync(ShortWait);
        Assert.Equal(2, received.Count);
        await TestWait.WaitUntilAsync(
            () => harness.Logs.Any(message => message.Contains("落盘失败")),
            message: "落盘异常被吞并由 log 记录");
        Assert.Empty(harness.Highlights.Snapshot);
        await TestWait.WaitUntilAsync(() => handle.MessageCount == 2, message: "管线继续工作");
    }

    private static void SeedSensitiveHighlightSettings(DanmakuManagerHarness harness)
    {
        harness.Settings.Seed(AppSettingKeys.HighlightMinShortWindowCount, "1");
        harness.Settings.Seed(AppSettingKeys.HighlightDensityMultiplier, "0.1");
        harness.Settings.Seed(AppSettingKeys.HighlightCooldownSeconds, "0");
    }

    /// <summary>等待指定 (平台, 房间) 在 Fake 工厂中成功连接，并返回对应客户端。</summary>
    private static async Task<FakeDanmakuClient> WaitForSessionClientAsync(
        DanmakuManagerHarness harness,
        PlatformType platform = PlatformType.Bilibili,
        string roomId = "1001")
    {
        await TestWait.WaitUntilAsync(
            () => harness.Factory.Clients.Any(client =>
                client.Platform == platform && client.RoomId == roomId && client.ConnectCount > 0),
            message: $"等待 {platform}:{roomId} 会话连接");

        return harness.Factory.Clients.First(client =>
            client.Platform == platform && client.RoomId == roomId && client.ConnectCount > 0);
    }

    /// <summary>收集句柄的批量消息，直到累计条数达到 expectedCount。</summary>
    private static Task<IReadOnlyList<DanmakuMessage>> CollectBatchesAsync(
        DanmakuSessionHandle handle,
        int expectedCount)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<DanmakuMessage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var collected = new List<DanmakuMessage>();
        var gate = new object();

        handle.MessagesReceived += batch =>
        {
            lock (gate)
            {
                collected.AddRange(batch);
                if (collected.Count >= expectedCount)
                {
                    completion.TrySetResult(collected.ToArray());
                }
            }
        };

        return completion.Task;
    }

    /// <summary>订阅下一次高能事件。</summary>
    private static Task<HighlightEvent> NextHighlightAsync(DanmakuManager manager)
    {
        var completion = new TaskCompletionSource<HighlightEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.HighlightTriggered += highlight => completion.TrySetResult(highlight);
        return completion.Task;
    }

    /// <summary>订阅下一次达到阈值的全局独轮车拦截计数。</summary>
    private static Task<int> NextUnicycleCountAsync(DanmakuManager manager, int atLeast)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.UnicycleBlockedCountChanged += count =>
        {
            if (count >= atLeast)
            {
                completion.TrySetResult(count);
            }
        };
        return completion.Task;
    }
}
