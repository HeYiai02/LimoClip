using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku.Filters;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

public class BilibiliUnicycleFilterTests
{
    private static readonly string[] SimilarTexts =
    {
        "主播今天真厉害啊",
        "主播今天真厉害呀",
        "主播今天真厉害哦",
        "主播今天真厉害额",
        "主播今天真厉害哈",
    };

    private static readonly string[] DistinctTexts =
    {
        "abcdefg123",
        "hijklmn456",
        "opqrstu789",
        "vwxyz00000",
    };

    [Fact]
    public void SimilarRepeat_TriggersMute_AndMuteExpiresAfterTenMinutes()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[0])));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[1])));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[2])));

        // 第 4 条相似内容（60 秒内 4 条相似度 > 80%）触发拦截并静默 10 分钟。
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[3])));

        Assert.Equal(1, filter.TrackedUserCount);
        Assert.Equal(1L, filter.BlockedCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("静默期内的正常弹幕")));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("静默期快结束的正常弹幕")));
        Assert.Equal(3L, filter.BlockedCount);

        // 静默期 = 触发时刻 + 10 分钟，越过即可恢复。
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("静默期结束后的正常弹幕")));
    }

    [Fact]
    public void RegularIntervalBurst_TriggersEvenWhenTextsAreDifferent()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);
        var results = new List<bool>();

        foreach (var text in DistinctTexts)
        {
            results.Add(filter.ShouldAllow(TestDanmaku.Create(text)));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(new[] { true, true, true, false }, results);
    }

    [Fact]
    public void AnonymousGiftAndOtherPlatforms_AreAlwaysAllowedAndNotTracked()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        for (var i = 0; i < 6; i++)
        {
            var text = SimilarTexts[i % SimilarTexts.Length];
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(text, uid: "0")));
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(text, uid: string.Empty)));
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(text, uid: "2002", isGiftOrSuperChat: true)));
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(text, uid: "3003", platform: PlatformType.Twitch)));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, filter.TrackedUserCount);
        Assert.Equal(0L, filter.BlockedCount);
    }

    [Fact]
    public void ShortEmotionWords_AreNeverBlocked_AndDoNotCountAsEvidence()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(filter.ShouldAllow(TestDanmaku.Create("66666")));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(filter.ShouldAllow(TestDanmaku.Create("？？？")));
        }

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("这是一条普通的长弹幕内容")));
        Assert.Equal(0L, filter.BlockedCount);
    }

    [Fact]
    public void LongTextBurst_ThreeMessagesWithinThirtySeconds_IsBlocked()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("这是一条超过十五个字符的长弹幕内容")));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("另一条同样很长的广告文案弹幕内容")));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("第三条用来测试长文本规则的弹幕内容")));

        Assert.Equal(1L, filter.BlockedCount);
    }

    [Fact]
    public void TripleIdenticalLongText_WithinFifteenSeconds_IsBlocked()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);
        const string text = "完全一模一样的弹幕内容";

        Assert.True(filter.ShouldAllow(TestDanmaku.Create(text)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(text)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create(text)));

        Assert.Equal(1L, filter.BlockedCount);
    }

    [Fact]
    public void IdleUserState_IsEvictedAfterFifteenMinutes()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("用户一的普通弹幕", uid: "u1")));
        Assert.Equal(1, filter.TrackedUserCount);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("用户二的普通弹幕", uid: "u2")));
        Assert.Equal(2, filter.TrackedUserCount);

        // u1 已有 16 分钟无发言，触发 5 分钟一次的驱逐检查后被清理。
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("用户三的普通弹幕", uid: "u3")));
        Assert.Equal(2, filter.TrackedUserCount);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("用户四的普通弹幕", uid: "u4")));
        Assert.Equal(1, filter.TrackedUserCount);
    }

    [Fact]
    public void RoomIsolation_SameUserInDifferentRooms_HasIndependentState()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);
        const string uid = "u9";

        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[0], uid: uid, roomId: "room-a")));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[1], uid: uid, roomId: "room-a")));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[2], uid: uid, roomId: "room-a")));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[0], uid: uid, roomId: "room-b")));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[1], uid: uid, roomId: "room-b")));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[2], uid: uid, roomId: "room-b")));

        Assert.Equal(2, filter.TrackedUserCount);

        // room-a 的第 4 条相似弹幕触发拦截，room-b 的状态不受影响。
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[3], uid: uid, roomId: "room-a")));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("666", uid: uid, roomId: "room-b")));
    }

    [Fact]
    public void Reset_ClearsTrackingAndMuteState()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[i])));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[3])));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, filter.TrackedUserCount);
        Assert.Equal(1L, filter.BlockedCount);

        filter.Reset();

        Assert.Equal(0, filter.TrackedUserCount);
        Assert.Equal(0L, filter.BlockedCount);
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[0])));
    }

    [Fact]
    public void IsEnabledFalse_AllowsMessages()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[i])));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[3])));

        filter.IsEnabled = false;
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(SimilarTexts[0])));
    }

    [Theory]
    [InlineData("[doge][打call]", "ee")]
    [InlineData("主播厉害[doge]", "主播厉害e")]
    [InlineData("没有表情的普通弹幕", "没有表情的普通弹幕")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FoldEmotes_FoldsEachBracketTokenToOneCharacter(string? input, string expected)
    {
        Assert.Equal(expected, BilibiliUnicycleFilter.FoldEmotes(input));
    }

    [Fact]
    public void InlineEmoteBurst_IsFoldedAndNotTreatedAsLongText()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);
        const string emoteOnly = "[打call][打call][打call][打call][打call][打call]";

        // 原始长度 36 字，折叠后为 6 个字，不应触发 30 秒长文本连发规则。
        for (var i = 0; i < 4; i++)
        {
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(emoteOnly)));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(0L, filter.BlockedCount);
        Assert.Equal(6, BilibiliUnicycleFilter.FoldEmotes(emoteOnly).Length);
    }

    [Fact]
    public void StickerMessages_AreExemptFromRepeatAndLongTextRules()
    {
        var clock = new FakeClock();
        var filter = new BilibiliUnicycleFilter(clock.Reader);
        const string stickerPlaceholder = "这是一个很长很长的大表情包纯表情弹幕";

        var emotes = new[] { new DanmakuEmote("赞", "https://example.invalid/sticker.png") };
        for (var i = 0; i < 5; i++)
        {
            Assert.True(filter.ShouldAllow(TestDanmaku.Create(stickerPlaceholder, isSticker: true, emotes: emotes)));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // 纯大表情包直接豁免，既不拦截也不进入单用户跟踪状态。
        Assert.Equal(0L, filter.BlockedCount);
        Assert.Equal(0, filter.TrackedUserCount);
    }

    [Theory]
    [InlineData("Abc，123！ 中文", "abc123中文")]
    [InlineData("！@#￥%……&*（）", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeText_KeepsOnlyLettersAndDigits(string? input, string expected)
    {
        Assert.Equal(expected, BilibiliUnicycleFilter.NormalizeText(input));
    }

    [Theory]
    [InlineData("abc", "abc", 1.0)]
    [InlineData("abcd", "abce", 0.75)]
    [InlineData("ab", "cd", 0.0)]
    public void ComputeSimilarity_ReturnsNormalizedLevenshteinSimilarity(string left, string right, double expected)
    {
        Assert.Equal(expected, BilibiliUnicycleFilter.ComputeSimilarity(left, right), 6);
    }
}
