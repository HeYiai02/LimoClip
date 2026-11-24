using System.Net;
using System.Security.Cryptography;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class PluginUpdateServiceTests
{
    private const string TestPluginDllName = "LiveMonitor.Platforms.TestPlugin.dll";
    private const string ManifestUrl = "https://example.com/providers-manifest.json";
    private const string PluginDllUrl = "https://example.com/plugins/plugin.dll";
    private const string MirrorPluginDllUrl = "https://mirror.example.com/plugins/plugin.dll";
    private const string AppVersion = "1.0.0";

    [Fact]
    public async Task CheckAsync_FindsUpdateForNewerPlugin()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes);
        using var http = CreateHttp(Manifest(entry), dllBytes);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Single(result.Updates);
        Assert.Equal("test-plugin", result.Updates[0].Entry.PluginId);
        Assert.Null(result.Updates[0].InstalledVersion);
        Assert.Equal(ManifestUrl, result.ManifestSource);
    }

    [Fact]
    public async Task CheckAsync_BuiltInNewer_SkipsPlugin()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes);
        fixture.Host.RegisterBuiltIn(new FakePlugin(PlatformType.YouTube, "9.9.9"));
        using var http = CreateHttp(Manifest(entry), dllBytes);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_MinAppVersionTooNew_SkipsPlugin()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes, minAppVersion: "9.0.0");
        using var fixture = new HostFixture(seed, dllBytes);
        using var http = CreateHttp(Manifest(entry), dllBytes);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_MirrorFallback_UsesSecondSource()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        const string mirrorUrl = "https://mirror.example.com/providers-manifest.json";
        using var fixture = new HostFixture(seed, dllBytes);
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.ToString().StartsWith("https://example.com", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : TestHttpMessageHandler.Json(Manifest(entry)));
        var service = new PluginUpdateService(
            new HttpClient(handler), fixture.Host, AppVersion, new[] { ManifestUrl, mirrorUrl });

        var result = await service.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(mirrorUrl, result.ManifestSource);
        Assert.Single(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_InvalidManifest_ReturnsFailure()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, _) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes);
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json("not a manifest"));
        var service = new PluginUpdateService(
            new HttpClient(handler), fixture.Host, AppVersion, new[] { ManifestUrl });

        var result = await service.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("无法获取平台插件清单", result.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_ValidPlugin_DownloadsInstallsAndApplies()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes);
        using var http = CreateHttp(Manifest(entry), dllBytes);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });
        var check = await service.CheckAsync();
        Assert.Single(check.Updates);

        var result = await service.InstallAsync(check.Updates);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains("test-plugin", result.Installed);
        Assert.Contains("test-plugin", result.Applied);
        Assert.Empty(result.Failures);
        Assert.Equal("1.2.3", fixture.Host.RegisteredPluginVersions[PlatformType.YouTube]);
    }

    [Fact]
    public async Task InstallAsync_TamperedDownload_RecordsFailure()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes);
        var tampered = (byte[])dllBytes.Clone();
        tampered[^1] ^= 0xFF;
        using var http = CreateHttp(Manifest(entry), tampered);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });
        var check = await service.CheckAsync();

        var result = await service.InstallAsync(check.Updates);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Installed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("签名校验失败", failure.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_ActiveTasks_DefersApply()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes);
        using var fixture = new HostFixture(seed, dllBytes, hasActiveTasks: static () => true);
        using var http = CreateHttp(Manifest(entry), dllBytes);
        var service = new PluginUpdateService(http.Client, fixture.Host, AppVersion, new[] { ManifestUrl });
        var check = await service.CheckAsync();

        var result = await service.InstallAsync(check.Updates);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains("test-plugin", result.Installed);
        Assert.Empty(result.Applied);
        Assert.Contains("test-plugin", result.Deferred);
        Assert.False(fixture.Host.RegisteredPluginVersions.ContainsKey(PlatformType.YouTube));
    }

    [Fact]
    public async Task InstallAsync_PrimaryDownloadFails_FallsBackToMirror()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes, mirrorDownloadUrl: MirrorPluginDllUrl);
        using var fixture = new HostFixture(seed, dllBytes);
        var handler = new TestHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri == ManifestUrl)
            {
                return TestHttpMessageHandler.Json(Manifest(entry));
            }

            if (uri == MirrorPluginDllUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dllBytes) };
            }

            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });
        var service = new PluginUpdateService(
            new HttpClient(handler), fixture.Host, AppVersion, new[] { ManifestUrl });
        var check = await service.CheckAsync();
        Assert.Single(check.Updates);

        var result = await service.InstallAsync(check.Updates);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains("test-plugin", result.Installed);
        Assert.Contains("test-plugin", result.Applied);
        Assert.Empty(result.Failures);
        Assert.Equal("1.2.3", fixture.Host.RegisteredPluginVersions[PlatformType.YouTube]);
    }

    [Fact]
    public async Task InstallAsync_PrimaryAndMirrorDownloadFail_RecordsFailure()
    {
        var dllBytes = ReadTestPluginBytes();
        if (dllBytes is null)
        {
            return;
        }

        var (seed, entry) = CreateSignedEntry(dllBytes, mirrorDownloadUrl: MirrorPluginDllUrl);
        using var fixture = new HostFixture(seed, dllBytes);
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.ToString() == ManifestUrl
                ? TestHttpMessageHandler.Json(Manifest(entry))
                : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var service = new PluginUpdateService(
            new HttpClient(handler), fixture.Host, AppVersion, new[] { ManifestUrl });
        var check = await service.CheckAsync();
        Assert.Single(check.Updates);

        var result = await service.InstallAsync(check.Updates);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Installed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("下载插件失败", failure.ErrorMessage);
    }

    private static string Manifest(ProviderPluginEntry entry)
        => $$"""
        {
          "manifestVersion": 1,
          "updatedAt": "2026-11-23T12:00:00Z",
          "plugins": [
            {
              "pluginId": "{{entry.PluginId}}",
              "platform": {{(int)entry.Platform}},
              "version": "{{entry.Version}}",
              "minAppVersion": "{{entry.MinAppVersion}}",
              "dllName": "{{entry.DllName}}",
              "downloadUrl": "{{entry.DownloadUrl}}",
              "mirrorDownloadUrl": "{{entry.MirrorDownloadUrl}}",
              "sha256": "{{entry.Sha256}}",
              "ed25519Signature": "{{entry.Ed25519Signature}}",
              "extraScripts": []
            }
          ]
        }
        """;

    private static (byte[] Seed, ProviderPluginEntry Entry) CreateSignedEntry(
        byte[] dllBytes,
        string minAppVersion = "1.0.0",
        string? mirrorDownloadUrl = null)
    {
        var seed = PluginTestKeys.NewSeed();
        var entry = new ProviderPluginEntry
        {
            PluginId = "test-plugin",
            Platform = PlatformType.YouTube,
            Version = "1.2.3",
            MinAppVersion = minAppVersion,
            DllName = PlatformPluginHost.PluginDllFileName,
            DownloadUrl = PluginDllUrl,
            MirrorDownloadUrl = mirrorDownloadUrl,
            Sha256 = Convert.ToHexString(SHA256.HashData(dllBytes)).ToLowerInvariant(),
            Ed25519Signature = PluginTestKeys.Sign(dllBytes, seed),
        };
        return (seed, entry);
    }

    private static HttpFixture CreateHttp(string manifestJson, byte[] pluginBytes)
    {
        var handler = new TestHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("plugin.dll", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pluginBytes) }
                : TestHttpMessageHandler.Json(manifestJson));
        return new HttpFixture(new HttpClient(handler));
    }

    private static byte[]? ReadTestPluginBytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, TestPluginDllName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private sealed class HttpFixture : IDisposable
    {
        public HttpFixture(HttpClient client) => Client = client;

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly string _root;

        public HostFixture(byte[] seed, byte[] dllBytes, Func<bool>? hasActiveTasks = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "limoclip-plugin-update-" + Guid.NewGuid().ToString("N"));
            var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
            Host = new PlatformPluginHost(
                new HttpClient(),
                "yt-dlp.exe",
                factory,
                new DanmakuClientFactory(),
                hasActiveTasks,
                pluginsRoot: _root,
                loader: new PlatformPluginLoader(PluginTestKeys.PublicKeyBase64(seed)));
            Factory = factory;
        }

        public PlatformPluginHost Host { get; }

        public PlatformFactory Factory { get; }

        public void Dispose()
        {
            Host.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响断言结果。
            }
        }
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
