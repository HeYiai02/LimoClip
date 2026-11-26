using LiveMonitor.Desktop.Player;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Player;

public sealed class PlayerPathResolverTests
{
    private const string BaseDir = @"C:\App";
    private const string Crt = @"C:\App\cacert.crt";
    private const string Pem = @"C:\App\cacert.pem";

    private static Func<string, bool> Exists(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return path => set.Contains(path);
    }

    [Fact]
    public void ResolveCaFile_ConfiguredAndExists_ReturnsConfigured()
    {
        var configured = @"D:\certs\custom.pem";

        var result = PlayerPathResolver.ResolveCaFile(configured, BaseDir, Exists(configured, Crt));

        Assert.Equal(configured, result);
    }

    [Fact]
    public void ResolveCaFile_ConfiguredMissing_FallsBackToBundledCrt()
    {
        var result = PlayerPathResolver.ResolveCaFile(@"D:\certs\missing.pem", BaseDir, Exists(Crt));

        Assert.Equal(Crt, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCaFile_ConfiguredBlank_UsesBundledCrt(string? configured)
    {
        var result = PlayerPathResolver.ResolveCaFile(configured, BaseDir, Exists(Crt, Pem));

        Assert.Equal(Crt, result);
    }

    [Fact]
    public void ResolveCaFile_OnlyPemExists_ReturnsPem()
    {
        var result = PlayerPathResolver.ResolveCaFile(null, BaseDir, Exists(Pem));

        Assert.Equal(Pem, result);
    }

    [Fact]
    public void ResolveCaFile_NothingExists_ReturnsNull()
    {
        var result = PlayerPathResolver.ResolveCaFile(null, BaseDir, Exists());

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCaFile_ConfiguredBeatsBundled_WhenBothExist()
    {
        var configured = @"D:\certs\custom.pem";

        var result = PlayerPathResolver.ResolveCaFile(configured, BaseDir, Exists(configured, Crt, Pem));

        Assert.Equal(configured, result);
    }

    [Fact]
    public void ResolveAudioMeterRoot_NormalLocalAppData_UsesLocalAppDataPath()
    {
        var localAppData = @"C:\Users\alice\AppData\Local";

        var root = PlayerPathResolver.ResolveAudioMeterRoot(localAppData, BaseDir);

        Assert.Equal(Path.Combine(localAppData, "LiveMonitor", "audio-meter"), root);
    }

    [Fact]
    public void ResolveAudioMeterRoot_TrailingSeparator_StillCombines()
    {
        var localAppData = @"C:\Users\alice\AppData\Local\";

        var root = PlayerPathResolver.ResolveAudioMeterRoot(localAppData, BaseDir);

        Assert.Equal(Path.Combine(localAppData, "LiveMonitor", "audio-meter"), root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAudioMeterRoot_MissingLocalAppData_FallsBackToBaseDirectory(string? localAppData)
    {
        var root = PlayerPathResolver.ResolveAudioMeterRoot(localAppData, BaseDir);

        Assert.Equal(Path.Combine(BaseDir, "audio-meter"), root);
    }

    [Fact]
    public void ResolveAudioMeterRoot_EmptyLocalAppDataWouldProduceRelativePath_FallsBackToBase()
    {
        // Path.Combine("", "LiveMonitor", "audio-meter") 得到相对路径 "LiveMonitor\audio-meter"，
        // 现有实现通过 StartsWith("LiveMonitor") 识别并回退到程序目录。
        var root = PlayerPathResolver.ResolveAudioMeterRoot(string.Empty, BaseDir);

        Assert.Equal(Path.Combine(BaseDir, "audio-meter"), root);
    }

    [Fact]
    public void ResolveAudioMeterRoot_DotLocalAppData_KeepsRelativeCombination()
    {
        // 现有语义：非空且不以 "LiveMonitor" 开头的相对值原样组合（历史行为，避免行为变化）。
        var root = PlayerPathResolver.ResolveAudioMeterRoot(".", BaseDir);

        Assert.Equal(Path.Combine(".", "LiveMonitor", "audio-meter"), root);
    }
}
