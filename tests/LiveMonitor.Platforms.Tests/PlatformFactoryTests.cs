using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

public sealed class PlatformFactoryTests
{
    [Fact]
    public void GetProvider_RoutesByUrlAndPlatform()
    {
        var bilibili = new BilibiliProvider(new HttpClient(new TestHttpMessageHandler(
            _ => throw new InvalidOperationException("路由测试不应发请求"))));
        var twitch = new TwitchProvider(new FakeYtDlpRunner());
        var factory = new PlatformFactory(new IPlatformProvider[] { bilibili, twitch });

        Assert.Same(bilibili, factory.GetProvider("https://live.bilibili.com/1"));
        Assert.Same(twitch, factory.GetProvider("https://www.twitch.tv/eslcs"));
        Assert.Same(bilibili, factory.GetProvider(PlatformType.Bilibili));
        Assert.Same(twitch, factory.GetProvider(PlatformType.Twitch));
        Assert.True(factory.CanHandle("7734200"));
        Assert.False(factory.CanHandle("https://example.com/live"));
    }

    [Fact]
    public void UnsupportedUrl_Throws()
    {
        var factory = new PlatformFactory(new IPlatformProvider[]
        {
            new BilibiliProvider(new HttpClient(new TestHttpMessageHandler(
                _ => throw new InvalidOperationException())))
        });

        Assert.Throws<NotSupportedException>(() => factory.GetProvider("https://example.com/live"));
        Assert.False(factory.TryGetProvider("https://example.com/live", out var provider));
        Assert.Null(provider);
    }

    [Fact]
    public void EmptyProviders_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PlatformFactory(Array.Empty<IPlatformProvider>()));
    }
}
