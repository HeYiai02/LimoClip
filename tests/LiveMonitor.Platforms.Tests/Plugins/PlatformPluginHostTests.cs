using System.Security.Cryptography;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class PlatformPluginHostTests
{
    private const string TestPluginDllName = "LiveMonitor.Platforms.TestPlugin.dll";

    [Fact]
    public void LoadInstalledPlugins_ValidSignedPlugin_RegistersProviderAndVersion()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var root = CreateTempRoot();
        try
        {
            WriteInstalledPlugin(root, entry, dllBytes);
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            var danmakuFactory = new DanmakuClientFactory();
            using var host = CreateHost(root, factory, danmakuFactory, seed, hasActiveTasks: static () => false);

            var loaded = host.LoadInstalledPlugins();

            Assert.Equal(1, loaded);
            Assert.Contains("test-plugin", host.InstalledPlugins.Keys);
            Assert.Equal("1.2.3", host.RegisteredPluginVersions[PlatformType.YouTube]);
            Assert.True(danmakuFactory.HasOverride(PlatformType.YouTube));

            var provider = factory.GetProvider("test-room-1");
            Assert.True(provider.CanHandle("test-room-1"));
            Assert.Equal(PlatformType.YouTube, provider.Platform);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LoadInstalledPlugins_ActiveTasks_DefersApply()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var root = CreateTempRoot();
        try
        {
            WriteInstalledPlugin(root, entry, dllBytes);
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            using var host = CreateHost(root, factory, new DanmakuClientFactory(), seed, hasActiveTasks: static () => true);

            var loaded = host.LoadInstalledPlugins();

            Assert.Equal(0, loaded);
            Assert.Contains("test-plugin", host.InstalledPlugins.Keys);
            Assert.False(host.RegisteredPluginVersions.ContainsKey(PlatformType.YouTube));

            Assert.False(host.TryApplyInstalledPlugin("test-plugin", out var error));
            Assert.Contains("正在进行的录制", error);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LoadInstalledPlugins_BuiltInNewer_KeepsBuiltIn()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var root = CreateTempRoot();
        try
        {
            WriteInstalledPlugin(root, entry, dllBytes);
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            using var host = CreateHost(root, factory, new DanmakuClientFactory(), seed, hasActiveTasks: static () => false);
            host.RegisterBuiltIn(new FakePlugin(PlatformType.YouTube, "9.9.9"));

            var loaded = host.LoadInstalledPlugins();

            Assert.Equal(0, loaded);
            Assert.False(host.IsNewerThanBuiltIn(entry));
            Assert.False(host.TryApplyInstalledPlugin("test-plugin", out var error));
            Assert.Contains("内置", error);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryInstall_WritesPluginFilesAndAllowsApply()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var root = CreateTempRoot();
        try
        {
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            using var host = CreateHost(root, factory, new DanmakuClientFactory(), seed, hasActiveTasks: static () => false);

            Assert.True(host.TryInstall(entry, dllBytes, out var installError), installError);
            Assert.True(File.Exists(Path.Combine(root, "test-plugin", PlatformPluginHost.PluginDllFileName)));
            Assert.True(File.Exists(Path.Combine(root, "test-plugin", PlatformPluginHost.PluginEntryFileName)));

            Assert.True(host.TryApplyInstalledPlugin("test-plugin", out var applyError), applyError);
            Assert.Equal("1.2.3", host.RegisteredPluginVersions[PlatformType.YouTube]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryInstall_TamperedDll_Rejected()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var root = CreateTempRoot();
        try
        {
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            using var host = CreateHost(root, factory, new DanmakuClientFactory(), seed, hasActiveTasks: static () => false);

            var tampered = (byte[])dllBytes.Clone();
            tampered[^1] ^= 0xFF;

            Assert.False(host.TryInstall(entry, tampered, out var error));
            Assert.Contains("签名校验失败", error);
            Assert.False(Directory.Exists(Path.Combine(root, "test-plugin")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryApplyInstalledPlugin_PluginIdMismatch_Fails()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        var mismatched = new ProviderPluginEntry
        {
            PluginId = "other-plugin",
            Platform = entry.Platform,
            Version = entry.Version,
            MinAppVersion = entry.MinAppVersion,
            DllName = entry.DllName,
            DownloadUrl = entry.DownloadUrl,
            Sha256 = entry.Sha256,
            Ed25519Signature = entry.Ed25519Signature,
        };
        var root = CreateTempRoot();
        try
        {
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            using var host = CreateHost(root, factory, new DanmakuClientFactory(), seed, hasActiveTasks: static () => false);

            Assert.True(host.TryInstall(mismatched, dllBytes, out var installError), installError);

            Assert.False(host.TryApplyInstalledPlugin("other-plugin", out var error));
            Assert.Contains("标识不一致", error);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static (byte[] Seed, ProviderPluginEntry Entry) CreateSignedEntry(byte[] dllBytes)
    {
        var seed = PluginTestKeys.NewSeed();
        var entry = new ProviderPluginEntry
        {
            PluginId = "test-plugin",
            Platform = PlatformType.YouTube,
            Version = "1.2.3",
            MinAppVersion = "1.0.0",
            DllName = PlatformPluginHost.PluginDllFileName,
            DownloadUrl = "https://example.com/plugins/plugin.dll",
            Sha256 = Convert.ToHexString(SHA256.HashData(dllBytes)).ToLowerInvariant(),
            Ed25519Signature = PluginTestKeys.Sign(dllBytes, seed),
        };
        return (seed, entry);
    }

    private static PlatformPluginHost CreateHost(
        string root,
        PlatformFactory factory,
        DanmakuClientFactory danmakuFactory,
        byte[] seed,
        Func<bool> hasActiveTasks)
        => new(
            new HttpClient(),
            "yt-dlp.exe",
            factory,
            danmakuFactory,
            hasActiveTasks,
            pluginsRoot: root,
            loader: new PlatformPluginLoader(PluginTestKeys.PublicKeyBase64(seed)));

    private static void WriteInstalledPlugin(string root, ProviderPluginEntry entry, byte[] dllBytes)
    {
        var directory = Path.Combine(root, entry.PluginId);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, PlatformPluginHost.PluginDllFileName), dllBytes);
        File.WriteAllText(
            Path.Combine(directory, PlatformPluginHost.PluginEntryFileName),
            JsonSerializer.Serialize(entry));
    }

    private static string CreateTempRoot()
        => Path.Combine(Path.GetTempPath(), "limoclip-plugin-host-" + Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言结果。
        }
    }

    private static byte[]? ReadTestPluginBytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, TestPluginDllName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private sealed class FakePlugin : IPlatformPlugin
    {
        public FakePlugin(PlatformType platform, string version)
        {
            Platform = platform;
            Version = version;
        }

        public PlatformType Platform { get; }

        public string PluginId => "fake-built-in";

        public string Version { get; }

        public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
            => new FakeProvider(Platform, "fake:");

        public IDanmakuClient? CreateDanmakuClient(string roomId, string? authCookie = null, HttpClient? httpClient = null)
            => null;
    }

    private sealed class FakeProvider : IPlatformProvider
    {
        private readonly string _prefix;

        public FakeProvider(PlatformType platform, string prefix)
        {
            Platform = platform;
            _prefix = prefix;
        }

        public PlatformType Platform { get; }

        public bool CanHandle(string roomUrl)
            => !string.IsNullOrWhiteSpace(roomUrl) &&
               roomUrl.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

        public string ExtractRoomId(string roomUrl)
            => CanHandle(roomUrl) ? roomUrl[_prefix.Length..] : throw new ArgumentException("无法识别的房间链接。", nameof(roomUrl));

        public Task<RoomStatus> GetRoomStatusAsync(string roomId, string? cookie = null, CancellationToken ct = default)
            => Task.FromResult(RoomStatus.Offline(roomId, Platform));
    }
}
