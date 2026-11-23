using System.Security.Cryptography;
using System.Text;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

public sealed class DouyuSignerTests
{
    private const string StubScript = """
        function ub98484234(rid, did, ts) {
          return 'v=220120261123&did=' + did + '&tt=' + ts + '&sign=' + CryptoJS.MD5(rid + '_' + did + '_' + ts).toString();
        }
        """;

    [Fact]
    public async Task SignAsync_ExecutesJsAndParsesParams()
    {
        var fetchCount = 0;
        using var signer = new DouyuSigner(null, (roomId, ct) =>
        {
            fetchCount++;
            Assert.Equal("9999", roomId);
            return Task.FromResult<string?>(StubScript);
        });

        var fields = await signer.SignAsync("9999");

        Assert.Equal(4, fields.Count);
        Assert.Equal("220120261123", fields["v"]);
        Assert.Matches("^[0-9a-f]{32}$", fields["did"]);
        Assert.True(long.TryParse(fields["tt"], out var tt));
        Assert.InRange(tt, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var expected = Md5("9999_" + fields["did"] + "_" + fields["tt"]);
        Assert.Equal(expected, fields["sign"]);
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task SignAsync_ReusesCachedScriptButFreshNonce()
    {
        var fetchCount = 0;
        using var signer = new DouyuSigner(null, (roomId, ct) =>
        {
            fetchCount++;
            return Task.FromResult<string?>(StubScript);
        });

        var first = await signer.SignAsync("9999");
        var second = await signer.SignAsync("9999");

        Assert.Equal(1, fetchCount);
        Assert.NotEqual(first["did"], second["did"]);
    }

    [Fact]
    public async Task SignAsync_ThrowsWhenScriptEmpty()
    {
        using var signer = new DouyuSigner(null, (roomId, ct) => Task.FromResult<string?>(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => signer.SignAsync("9999"));
        Assert.Contains("签名脚本", error.Message);
    }

    [Fact]
    public async Task SignAsync_ThrowsWhenFunctionMissing()
    {
        using var signer = new DouyuSigner(null, (roomId, ct) => Task.FromResult<string?>("var x = 1;"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => signer.SignAsync("9999"));
    }

    [Fact]
    public async Task SignAsync_RejectsNonNumericRoom()
    {
        using var signer = new DouyuSigner(null, (roomId, ct) => Task.FromResult<string?>(StubScript));
        await Assert.ThrowsAsync<ArgumentException>(() => signer.SignAsync("https://www.douyu.com/9999"));
    }

    private static string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
