using LiveMonitor.Core.Enums;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class ProvidersManifestTests
{
    private static readonly string ValidSignature = Convert.ToBase64String(new byte[64]);
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string BuildManifestJson(string manifestVersion = "1", string pluginsJson = "")
    {
        if (string.IsNullOrEmpty(pluginsJson))
        {
            pluginsJson = $$"""
            [
              {
                "pluginId": "bilibili-native",
                "platform": 1,
                "version": "1.1.0",
                "minAppVersion": "0.5.0",
                "dllName": "LiveMonitor.Platforms.Bilibili.dll",
                "downloadUrl": "https://example.com/plugins/bilibili.zip",
                "sha256": "{{ValidSha}}",
                "ed25519Signature": "{{ValidSignature}}",
                "extraScripts": [
                  {
                    "name": "bilibili-wbi.js",
                    "downloadUrl": "https://example.com/plugins/bilibili-wbi.js",
                    "sha256": "{{ValidSha}}",
                    "ed25519Signature": "{{ValidSignature}}"
                  }
                ]
              },
              {
                "pluginId": "youtube",
                "platform": 6,
                "version": "1.0.0",
                "minAppVersion": "0.5.0",
                "dllName": "LiveMonitor.Platforms.YouTube.dll",
                "downloadUrl": "https://example.com/plugins/youtube.zip",
                "sha256": "{{ValidSha}}",
                "ed25519Signature": "{{ValidSignature}}",
                "extraScripts": []
              }
            ]
            """;
        }

        return $$"""
        {
          "manifestVersion": {{manifestVersion}},
          "updatedAt": "2026-11-23T12:00:00Z",
          "plugins": {{pluginsJson}}
        }
        """;
    }

    [Fact]
    public void Parse_ValidManifest_ExposesEntries()
    {
        var manifest = ProvidersManifest.Parse(BuildManifestJson());

        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal(2, manifest.Plugins.Count);
        Assert.NotNull(manifest.UpdatedAt);

        var bilibili = manifest.Find("bilibili-native");
        Assert.NotNull(bilibili);
        Assert.Equal(PlatformType.Bilibili, bilibili!.Platform);
        Assert.Equal("1.1.0", bilibili.Version);
        Assert.Equal("LiveMonitor.Platforms.Bilibili.dll", bilibili.DllName);
        Assert.Single(bilibili.ExtraScripts);
        Assert.Equal("bilibili-wbi.js", bilibili.ExtraScripts[0].Name);

        Assert.NotNull(manifest.Find("YOUTUBE"));
        Assert.Null(manifest.Find("missing-plugin"));
        Assert.Null(manifest.Find(null));
        Assert.Empty(manifest.Validate());
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => ProvidersManifest.Parse("{ not json"));
        Assert.Throws<InvalidDataException>(() => ProvidersManifest.Parse(string.Empty));
    }

    [Fact]
    public void Validate_UnsupportedManifestVersion_ReportsError()
    {
        var manifest = ProvidersManifest.Parse(BuildManifestJson(manifestVersion: "2"));

        var errors = manifest.Validate();

        Assert.Contains(errors, e => e.Contains("manifestVersion"));
    }

    [Fact]
    public void Validate_EmptyPlugins_IsAllowed()
    {
        var manifest = ProvidersManifest.Parse(BuildManifestJson(pluginsJson: "[]"));

        var errors = manifest.Validate();

        Assert.Empty(errors);
        Assert.Empty(manifest.Plugins);
    }

    [Fact]
    public void Validate_BadEntryFields_ReportsErrors()
    {
        var manifest = new ProvidersManifest
        {
            ManifestVersion = 1,
            Plugins = new[]
            {
                new ProviderPluginEntry
                {
                    PluginId = "broken",
                    Platform = (PlatformType)99,
                    Version = "",
                    MinAppVersion = "",
                    DllName = "sub/dir/plugin.dll",
                    DownloadUrl = "http://example.com/file.zip",
                    Sha256 = "not-a-sha",
                    Ed25519Signature = "bad-signature",
                    ExtraScripts = new[]
                    {
                        new ProviderManifestExtraScript
                        {
                            Name = "",
                            DownloadUrl = "not-a-url",
                            Sha256 = "x",
                            Ed25519Signature = "y",
                        },
                    },
                },
            },
        };

        var errors = manifest.Validate();

        Assert.Contains(errors, e => e.Contains("platform"));
        Assert.Contains(errors, e => e.Contains("dllName"));
        Assert.Contains(errors, e => e.Contains("downloadUrl"));
        Assert.Contains(errors, e => e.Contains("sha256"));
        Assert.Contains(errors, e => e.Contains("ed25519Signature"));
        Assert.Contains(errors, e => e.Contains("extraScripts[0]"));
    }

    [Fact]
    public void Validate_InvalidMirrorDownloadUrl_ReportsError()
    {
        var entry = new ProviderPluginEntry
        {
            PluginId = "mirror-broken",
            Platform = PlatformType.Twitch,
            Version = "1.0.0",
            MinAppVersion = "0.5.0",
            DllName = "LiveMonitor.Platforms.Twitch.dll",
            DownloadUrl = "https://example.com/twitch.zip",
            MirrorDownloadUrl = "http://mirror.example.com/twitch.zip",
            Sha256 = ValidSha,
            Ed25519Signature = ValidSignature,
        };
        var manifest = new ProvidersManifest { ManifestVersion = 1, Plugins = new[] { entry } };

        var errors = manifest.Validate();

        Assert.Contains(errors, e => e.Contains("mirrorDownloadUrl"));
    }

    [Fact]
    public void Parse_MirrorDownloadUrl_IsExposed()
    {
        var pluginsJson = $$"""
        [
          {
            "pluginId": "mirror-plugin",
            "platform": 5,
            "version": "1.0.0",
            "minAppVersion": "0.5.0",
            "dllName": "LiveMonitor.Platforms.Mirror.dll",
            "downloadUrl": "https://example.com/plugins/mirror.dll",
            "mirrorDownloadUrl": "https://gitee.com/HeYiai/limoclip-plugins/raw/master/plugins/mirror.dll",
            "sha256": "{{ValidSha}}",
            "ed25519Signature": "{{ValidSignature}}",
            "extraScripts": []
          }
        ]
        """;

        var manifest = ProvidersManifest.Parse(BuildManifestJson(pluginsJson: pluginsJson));

        var entry = Assert.Single(manifest.Plugins);
        Assert.Equal(
            "https://gitee.com/HeYiai/limoclip-plugins/raw/master/plugins/mirror.dll",
            entry.MirrorDownloadUrl);
        Assert.Empty(manifest.Validate());
    }

    [Fact]
    public void Validate_DuplicatePluginId_ReportsError()
    {
        var entry = new ProviderPluginEntry
        {
            PluginId = "dup",
            Platform = PlatformType.Twitch,
            Version = "1.0.0",
            MinAppVersion = "0.5.0",
            DllName = "LiveMonitor.Platforms.Twitch.dll",
            DownloadUrl = "https://example.com/twitch.zip",
            Sha256 = ValidSha,
            Ed25519Signature = ValidSignature,
        };
        var manifest = new ProvidersManifest { ManifestVersion = 1, Plugins = new[] { entry, entry } };

        var errors = manifest.Validate();

        Assert.Contains(errors, e => e.Contains("重复"));
    }

    [Fact]
    public void EnsureValid_InvalidManifest_ThrowsWithDetails()
    {
        var manifest = ProvidersManifest.Parse(BuildManifestJson(manifestVersion: "9"));

        var ex = Assert.Throws<InvalidDataException>(manifest.EnsureValid);

        Assert.Contains("manifestVersion", ex.Message);
    }
}
