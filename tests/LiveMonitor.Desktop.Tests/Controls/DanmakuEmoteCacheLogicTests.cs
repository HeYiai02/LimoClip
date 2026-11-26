using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

public sealed class DanmakuEmoteCacheLogicTests
{
    [Theory]
    [InlineData("https://i0.hdslb.com/bfs/emote/abc.png", "https://i0.hdslb.com/bfs/emote/abc.png")]
    [InlineData("  https://i0.hdslb.com/abc.png  ", "https://i0.hdslb.com/abc.png")]
    [InlineData("//i0.hdslb.com/abc.png", "https://i0.hdslb.com/abc.png")]
    [InlineData(" //i0.hdslb.com/abc.png\t", "https://i0.hdslb.com/abc.png")]
    [InlineData("http://i0.hdslb.com/abc.png", "http://i0.hdslb.com/abc.png")]
    [InlineData("", "")]
    public void NormalizeUrl_TrimsAndCompletesProtocolRelative(string input, string expected)
    {
        Assert.Equal(expected, DanmakuEmoteCacheLogic.NormalizeUrl(input));
    }

    [Fact]
    public void NormalizeUrl_ProtocolRelativeAndAbsoluteShareDiskFile()
    {
        var relative = DanmakuEmoteCacheLogic.DiskFileName("//i0.hdslb.com/abc.png");
        var absolute = DanmakuEmoteCacheLogic.DiskFileName("https://i0.hdslb.com/abc.png");

        Assert.Equal(absolute, relative);
    }

    [Fact]
    public void DiskFileName_IsLowercaseSha1HexWithImgExtension()
    {
        var name = DanmakuEmoteCacheLogic.DiskFileName("https://i0.hdslb.com/abc.png");

        Assert.EndsWith(DanmakuEmoteCacheLogic.DiskFileExtension, name, StringComparison.Ordinal);
        Assert.Equal(40 + DanmakuEmoteCacheLogic.DiskFileExtension.Length, name.Length);
        Assert.Matches("^[0-9a-f]{40}\\.img$", name);
    }

    [Fact]
    public void DiskFileName_IsStableAndDiffersByUrl()
    {
        var first = DanmakuEmoteCacheLogic.DiskFileName("https://example.com/a.png");
        var second = DanmakuEmoteCacheLogic.DiskFileName("https://example.com/a.png");
        var other = DanmakuEmoteCacheLogic.DiskFileName("https://example.com/b.png");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, 59_999L, false)]
    [InlineData(0L, 60_000L, true)]
    [InlineData(0L, 60_001L, true)]
    [InlineData(1_000L, 1_000L - 1_000L, false)]
    [InlineData(5_000L, 64_999L, false)]
    [InlineData(5_000L, 65_000L, true)]
    [InlineData(10_000L, 1_000L, false)]
    public void ShouldRetryAfterFailure_OnlyAfterCooldown(long failedAt, long now, bool expected)
    {
        Assert.Equal(expected, DanmakuEmoteCacheLogic.ShouldRetryAfterFailure(failedAt, now));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 1 }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0 }, true)]
    public void HasUsablePayload_RequiresNonEmptyBytes(byte[]? payload, bool expected)
    {
        Assert.Equal(expected, DanmakuEmoteCacheLogic.HasUsablePayload(payload));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1_999, true)]
    [InlineData(2_000, true)]
    [InlineData(2_001, false)]
    [InlineData(10_000, false)]
    public void CanWriteDisk_RespectsSoftLimit(int existingFileCount, bool expected)
    {
        Assert.Equal(expected, DanmakuEmoteCacheLogic.CanWriteDisk(existingFileCount));
    }

    [Fact]
    public void Constants_AreStable()
    {
        Assert.Equal(200, DanmakuEmoteCacheLogic.MaxMemoryEntries);
        Assert.Equal(2_000, DanmakuEmoteCacheLogic.DiskCacheMaxFiles);
        Assert.Equal(60_000, DanmakuEmoteCacheLogic.FailedRetryMilliseconds);
        Assert.Equal(".img", DanmakuEmoteCacheLogic.DiskFileExtension);
    }
}
