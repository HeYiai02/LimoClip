using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class FfmpegLocatorTests
{
    [Fact]
    public void TryLocateCaBundle_FindsBundledCertificate()
    {
        if (!FfmpegLocator.TryLocateCaBundle(out var caBundlePath))
        {
            // 仓库/发布目录未带 cacert.crt 时跳过（与 ffmpeg 缺失相同的策略）。
            return;
        }

        Assert.True(Path.IsPathRooted(caBundlePath));
        Assert.True(File.Exists(caBundlePath));
        Assert.Equal("cacert.crt", Path.GetFileName(caBundlePath));
    }

    [Fact]
    public void LocateCaBundle_ReturnsSamePathAsTryLocate()
    {
        if (!FfmpegLocator.TryLocateCaBundle(out var expected))
        {
            return;
        }

        Assert.Equal(expected, FfmpegLocator.LocateCaBundle());
    }
}
