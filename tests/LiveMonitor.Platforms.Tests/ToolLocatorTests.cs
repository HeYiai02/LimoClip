using System.IO;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class ToolLocatorTests
{
    [Fact]
    public void ResolveYtDlpPath_ExplicitExistingPath_IsReturned()
    {
        var probe = Path.Combine(Path.GetTempPath(), $"limoclip-tool-{Guid.NewGuid():N}.exe");
        File.WriteAllText(probe, "stub");
        try
        {
            Assert.Equal(probe, ToolLocator.ResolveYtDlpPath(probe));
            Assert.Equal(probe, ToolLocator.FindYtDlpPath(probe));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [Fact]
    public void ResolveYtDlpPath_NoExistingCandidate_ReturnsRootedDefault()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"limoclip-missing-{Guid.NewGuid():N}", "yt-dlp.exe");

        var resolved = ToolLocator.ResolveYtDlpPath(missing);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("yt-dlp.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserDirectories_AreUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, ToolLocator.AppDataRootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.Combine(ToolLocator.AppDataRootDirectory, "plugins"),
            ToolLocator.PluginsRootDirectory);
    }
}
