using LiveMonitor.Core.Entities;
using LiveMonitor.Danmaku.Filters;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

public class KeywordFilterTests
{
    private static DanmakuBlockKeyword Plain(string keyword) => new() { Keyword = keyword };

    private static DanmakuBlockKeyword RegexWord(string pattern) => new() { Keyword = pattern, IsRegex = true };

    [Fact]
    public void ShouldAllow_BlocksContainsWords_IgnoreCaseAndChinese()
    {
        var filter = new KeywordFilter(new[] { Plain("广告"), Plain("spam") });

        Assert.False(filter.ShouldAllow(TestDanmaku.Create("这是一条广告弹幕")));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("SPAM everywhere")));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("混合 Spam 大小写")));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("正常弹幕内容")));
    }

    [Fact]
    public void ShouldAllow_RegexWord_BlocksMatchingContent()
    {
        var filter = new KeywordFilter(new[] { RegexWord(@"^\d{6}$") });

        Assert.False(filter.ShouldAllow(TestDanmaku.Create("123456")));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("12345")));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("房间123456")));
    }

    [Theory]
    [InlineData("a.*b", true)]
    [InlineData(@"^\d{6}$", true)]
    [InlineData("(", false)]
    [InlineData("[", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidRegex_ValidatesPattern(string? pattern, bool expected)
    {
        Assert.Equal(expected, KeywordFilter.IsValidRegex(pattern));
    }

    [Fact]
    public void Update_InvalidRegex_DoesNotThrow_AndOldSnapshotIsReplaced()
    {
        var logs = new List<string>();
        var filter = new KeywordFilter(new[] { Plain("广告") }, logs.Add);

        filter.Update(new[] { RegexWord("("), Plain("剧透") });

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("正常弹幕")));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("这里有剧透")));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create("这里有广告")));
        Assert.Single(logs);
    }

    [Fact]
    public void Update_HotReload_ImmediatelyTakesEffect()
    {
        var filter = new KeywordFilter(new[] { Plain("广告") });
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("广告")));

        filter.Update(new[] { Plain("剧透") });

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("广告")));
        Assert.False(filter.ShouldAllow(TestDanmaku.Create("剧透")));
    }

    [Fact]
    public void EmptyAndWhitespaceKeywords_AreIgnored()
    {
        var filter = new KeywordFilter(new[] { Plain(""), Plain("   "), Plain("\t") });

        Assert.True(filter.ShouldAllow(TestDanmaku.Create("任意内容")));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(string.Empty)));
    }

    [Fact]
    public void RegexMatchTimeout_IsSkippedAndLoggedOnce()
    {
        var logs = new List<string>();
        var filter = new KeywordFilter(new[] { RegexWord(@"^(a+)+$") }, logs.Add);
        var content = new string('a', 512) + "!";

        // 灾难性回溯的正则命中 100ms 超时：只跳过本次匹配，不能抛异常。
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(content)));
        Assert.True(filter.ShouldAllow(TestDanmaku.Create(content)));
        Assert.Single(logs);
    }
}
