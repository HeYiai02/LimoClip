using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class PlatformFactoryHotSwapTests
{
    [Fact]
    public void ReplaceProvider_ExistingPlatform_ReplacesInPlace()
    {
        var bilibili = new FakeProvider(PlatformType.Bilibili, "bili:");
        var twitch = new FakeProvider(PlatformType.Twitch, "twitch:");
        var factory = new PlatformFactory(new IPlatformProvider[] { bilibili, twitch });

        var pluginBilibili = new FakeProvider(PlatformType.Bilibili, "bili2:");
        factory.ReplaceProvider(pluginBilibili);

        Assert.Same(pluginBilibili, factory.GetProvider(PlatformType.Bilibili));
        Assert.Same(twitch, factory.GetProvider(PlatformType.Twitch));
        Assert.Equal(2, factory.Providers.Count);
        Assert.False(factory.CanHandle("bili:1"));
        Assert.True(factory.CanHandle("bili2:1"));
    }

    [Fact]
    public void ReplaceProvider_NewPlatform_Appends()
    {
        var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
        var youtube = new FakeProvider(PlatformType.YouTube, "yt:");

        factory.ReplaceProvider(youtube);

        Assert.Same(youtube, factory.GetProvider(PlatformType.YouTube));
        Assert.Same(youtube, factory.GetProvider("yt:abc"));
        Assert.Equal(2, factory.Providers.Count);
    }

    [Fact]
    public void ReplaceProviders_DeduplicatesByPlatform_LastWins()
    {
        var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });
        var first = new FakeProvider(PlatformType.YouTube, "yt1:");
        var second = new FakeProvider(PlatformType.YouTube, "yt2:");

        factory.ReplaceProviders(new IPlatformProvider[] { first, second });

        Assert.Same(second, factory.GetProvider(PlatformType.YouTube));
        Assert.Single(factory.Providers);
    }

    [Fact]
    public void ReplaceProviders_Empty_Throws()
    {
        var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });

        Assert.Throws<ArgumentException>(() => factory.ReplaceProviders(Array.Empty<IPlatformProvider>()));
    }

    [Fact]
    public void RemoveProvider_RemainingPlatforms_Removes()
    {
        var factory = new PlatformFactory(new IPlatformProvider[]
        {
            new FakeProvider(PlatformType.Bilibili, "bili:"),
            new FakeProvider(PlatformType.Twitch, "twitch:"),
        });

        Assert.True(factory.RemoveProvider(PlatformType.Twitch));
        Assert.False(factory.RemoveProvider(PlatformType.Twitch));
        Assert.Single(factory.Providers);
        Assert.Throws<NotSupportedException>(() => factory.GetProvider(PlatformType.Twitch));
    }

    [Fact]
    public void RemoveProvider_LastPlatform_Throws()
    {
        var factory = new PlatformFactory(new IPlatformProvider[] { new FakeProvider(PlatformType.Bilibili, "bili:") });

        Assert.Throws<InvalidOperationException>(() => factory.RemoveProvider(PlatformType.Bilibili));
        Assert.Single(factory.Providers);
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
