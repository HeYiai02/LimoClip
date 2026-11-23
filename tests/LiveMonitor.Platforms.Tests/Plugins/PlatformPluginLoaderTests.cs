using System.Security;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class PlatformPluginLoaderTests
{
    private const string TestPluginDllName = "LiveMonitor.Platforms.TestPlugin.dll";

    [Fact]
    public void LoadVerifiedPlugin_ValidSignature_LoadsPluginFromMemory()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(dllBytes, seed);

        var loader = new PlatformPluginLoader(publicKey);
        using var handle = loader.LoadVerifiedPlugin(dllBytes, signature, null, "test-plugin.dll");

        Assert.IsAssignableFrom<IPlatformPlugin>(handle.Plugin);
        Assert.Equal("test-plugin", handle.PluginId);
        Assert.Equal("1.2.3", handle.Version);
        Assert.Equal(PlatformType.YouTube, handle.Platform);
        Assert.NotEqual(typeof(IPlatformPlugin).Assembly, handle.Plugin.GetType().Assembly);

        var provider = handle.Plugin.CreateStreamProvider(new HttpClient(), "yt-dlp.exe");
        Assert.True(provider.CanHandle("test-room-1"));
        Assert.Null(handle.Plugin.CreateDanmakuClient("room-1"));
    }

    [Fact]
    public void LoadVerifiedPlugin_FromFile_LoadsPlugin()
    {
        var path = FindTestPluginPath();
        if (path is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(File.ReadAllBytes(path), seed);

        var loader = new PlatformPluginLoader(publicKey);
        using var handle = loader.LoadVerifiedPlugin(path, signature);

        Assert.Equal("test-plugin", handle.PluginId);
        Assert.Equal(path, handle.DllPath);
    }

    [Fact]
    public void LoadVerifiedPlugin_TamperedDll_ThrowsSecurityException()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(dllBytes, seed);

        var tampered = (byte[])dllBytes.Clone();
        tampered[^1] ^= 0xFF;

        var loader = new PlatformPluginLoader(publicKey);
        Assert.Throws<SecurityException>(() =>
            loader.LoadVerifiedPlugin(tampered, signature, null, "tampered.dll"));
    }

    [Fact]
    public void LoadVerifiedPlugin_WrongTrustedKey_ThrowsSecurityException()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var signature = PluginTestKeys.Sign(dllBytes, seed);
        var otherPublicKey = PluginTestKeys.PublicKeyBase64(PluginTestKeys.NewSeed());

        var loader = new PlatformPluginLoader(otherPublicKey);
        Assert.Throws<SecurityException>(() =>
            loader.LoadVerifiedPlugin(dllBytes, signature, null, "test-plugin.dll"));
    }

    [Fact]
    public void LoadVerifiedPlugin_Sha256Mismatch_ThrowsSecurityException()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(dllBytes, seed);

        var loader = new PlatformPluginLoader(publicKey);
        Assert.Throws<SecurityException>(() =>
            loader.LoadVerifiedPlugin(dllBytes, signature, new string('0', 64), "test-plugin.dll"));
    }

    [Fact]
    public void LoadVerifiedPlugin_InvalidSignature_ThrowsSecurityException()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var loader = new PlatformPluginLoader(PluginTestKeys.PublicKeyBase64(PluginTestKeys.NewSeed()));

        Assert.Throws<SecurityException>(() =>
            loader.LoadVerifiedPlugin(dllBytes, Convert.ToBase64String(new byte[64]), null, "test-plugin.dll"));
    }

    [Fact]
    public void LoadVerifiedPlugin_MissingFile_ThrowsFileNotFoundException()
    {
        var loader = new PlatformPluginLoader(PluginTestKeys.PublicKeyBase64(PluginTestKeys.NewSeed()));

        Assert.Throws<FileNotFoundException>(() =>
            loader.LoadVerifiedPlugin(Path.Combine(Path.GetTempPath(), "definitely-missing-plugin.dll"), "AAAA"));
    }

    [Fact]
    public void TryLoadVerifiedPlugin_BadSignature_ReturnsFalseWithoutThrowing()
    {
        var path = FindTestPluginPath();
        if (path is null)
        {
            return;
        }

        var loader = new PlatformPluginLoader(PluginTestKeys.PublicKeyBase64(PluginTestKeys.NewSeed()));

        var ok = loader.TryLoadVerifiedPlugin(
            path, Convert.ToBase64String(new byte[64]), null, out var handle, out var error);

        Assert.False(ok);
        Assert.Null(handle);
        Assert.Contains("签名校验失败", error);
    }

    [Fact]
    public void TryLoadVerifiedPlugin_ValidSignature_ReturnsHandle()
    {
        var path = FindTestPluginPath();
        if (path is null)
        {
            return;
        }

        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(File.ReadAllBytes(path), seed);
        var loader = new PlatformPluginLoader(publicKey);

        var ok = loader.TryLoadVerifiedPlugin(path, signature, null, out var handle, out var error);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.NotNull(handle);
        using (handle)
        {
            Assert.Equal("test-plugin", handle!.PluginId);
        }
    }

    [Fact]
    public void LoadVerifiedPlugin_SignedAssemblyWithoutPluginEntry_ThrowsInvalidOperationException()
    {
        // 用一个不含 IPlatformPlugin 实现的程序集（LiveMonitor.Core.dll）作为“插件”。
        var corePath = Path.Combine(AppContext.BaseDirectory, "LiveMonitor.Core.dll");
        if (!File.Exists(corePath))
        {
            return;
        }

        var payload = File.ReadAllBytes(corePath);
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(payload, seed);

        var loader = new PlatformPluginLoader(publicKey);
        Assert.Throws<InvalidOperationException>(() =>
            loader.LoadVerifiedPlugin(payload, signature, null, "LiveMonitor.Core.dll"));
    }

    private static string? FindTestPluginPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, TestPluginDllName);
        return File.Exists(path) ? path : null;
    }

    private static byte[]? ReadTestPluginBytes()
    {
        var path = FindTestPluginPath();
        return path is null ? null : File.ReadAllBytes(path);
    }
}
