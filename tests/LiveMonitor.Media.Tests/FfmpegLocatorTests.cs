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

    /// <summary>
    /// CI 守卫：持续集成环境（<c>CI=true</c>）必须能定位到随包 ffmpeg。
    /// 背景：大量 FFmpeg 相关用例在找不到 ffmpeg 时会直接 <c>return</c>，在测试报告里仍记为 Passed，
    /// 无法发现「CI 没装 ffmpeg、整批用例静默跳过」的问题；因此 CI 用本守卫显式兜底。
    /// 本地开发机允许缺少 ffmpeg（此时跳过），不要求所有贡献者都下载。
    /// </summary>
    [Fact]
    public void Locate_UnderCiEnvironment_MustResolveFfmpeg()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.True(
            FfmpegLocator.TryLocate(out var located),
            $"CI 环境必须解析到 ffmpeg（搜索路径见 FfmpegLocator 注释），当前工作目录：{Environment.CurrentDirectory}");
        Assert.True(File.Exists(located));
        Assert.Equal(Path.GetFullPath(located), Path.GetFullPath(FfmpegLocator.Locate()));
    }
}
