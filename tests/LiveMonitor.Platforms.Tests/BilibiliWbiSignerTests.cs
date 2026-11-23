using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>B 站 WBI 签名算法测试：置换 key 与查询串签名结果均与线上算法一致。</summary>
public sealed class BilibiliWbiSignerTests
{
    // 真实 nav 接口返回的一对密钥（2026-11 实测）。
    private const string ImgKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932caff0ff746eab6f01bf08b70ac45";

    [Fact]
    public void CreateMixinKey_UsesOfficialPermutation()
    {
        var mixinKey = BilibiliWbiSigner.CreateMixinKey(ImgKey, SubKey);

        Assert.Equal("ea1db124af3c7062474693fa704f4ff8", mixinKey);
        Assert.Equal(32, mixinKey.Length);
    }

    [Fact]
    public void Sign_ProducesDeterministicQueryWithWtsAndWrid()
    {
        var query = BilibiliWbiSigner.Sign(
            new[]
            {
                new KeyValuePair<string, string>("web_location", "444.8"),
                new KeyValuePair<string, string>("id", "1234"),
                new KeyValuePair<string, string>("type", "0"),
            },
            "ea1db124af3c7062474693fa704f4ff8",
            1778051289);

        Assert.Equal(
            "id=1234&type=0&web_location=444.8&wts=1778051289&w_rid=6fda64157c113669005be917dc22e9d8",
            query);
    }

    [Fact]
    public void Sign_FiltersSpecialCharactersBeforeEncoding()
    {
        var query = BilibiliWbiSigner.Sign(
            new[] { new KeyValuePair<string, string>("keyword", "a!b'c(d)e*f g") },
            "ea1db124af3c7062474693fa704f4ff8",
            1778051289);

        // !'()* 被剔除，空格按 RFC3986 编码为 %20。
        Assert.Contains("keyword=abcdef%20g&", query);
    }

    [Fact]
    public void Sign_DifferentTimestampChangesWRid()
    {
        var parameters = new[] { new KeyValuePair<string, string>("id", "1234") };
        var first = BilibiliWbiSigner.Sign(parameters, "ea1db124af3c7062474693fa704f4ff8", 1778051289);
        var second = BilibiliWbiSigner.Sign(parameters, "ea1db124af3c7062474693fa704f4ff8", 1778051290);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateMixinKey_RejectsIncompleteKeys()
    {
        Assert.Throws<ArgumentException>(() => BilibiliWbiSigner.CreateMixinKey(string.Empty, SubKey));
        Assert.Throws<ArgumentException>(() => BilibiliWbiSigner.CreateMixinKey(ImgKey, "short"));
    }
}
