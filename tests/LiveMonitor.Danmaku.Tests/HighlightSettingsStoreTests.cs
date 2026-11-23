using LiveMonitor.Core;
using LiveMonitor.Core.Models;
using LiveMonitor.Danmaku;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

public class HighlightSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_EmptyRepository_ReturnsDefaults()
    {
        var repository = new InMemoryAppSettingsRepository();

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(10, options.ShortWindowSeconds);
        Assert.Equal(180, options.BaselineWindowSeconds);
        Assert.Equal(3.0, options.DensityMultiplier, 6);
        Assert.Equal(15, options.MinShortWindowCount);
        Assert.Equal(60, options.CooldownSeconds);
        Assert.Equal(new[] { "666", "草", "神回", "Pog" }, options.BoostKeywords);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllValues()
    {
        var repository = new InMemoryAppSettingsRepository();
        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = 5,
            BaselineWindowSeconds = 120,
            DensityMultiplier = 2.5,
            MinShortWindowCount = 8,
            CooldownSeconds = 30,
            BoostKeywords = new[] { "666", "草" },
        };

        await HighlightSettingsStore.SaveAsync(repository, options);
        var loaded = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(options.ShortWindowSeconds, loaded.ShortWindowSeconds);
        Assert.Equal(options.BaselineWindowSeconds, loaded.BaselineWindowSeconds);
        Assert.Equal(options.DensityMultiplier, loaded.DensityMultiplier, 6);
        Assert.Equal(options.MinShortWindowCount, loaded.MinShortWindowCount);
        Assert.Equal(options.CooldownSeconds, loaded.CooldownSeconds);
        Assert.Equal(new[] { "666", "草" }, loaded.BoostKeywords);
        Assert.Equal(6, repository.Snapshot.Count);
    }

    [Fact]
    public async Task LoadAsync_InvalidValues_FallBackAndClamp()
    {
        var repository = new InMemoryAppSettingsRepository();
        repository.Seed(AppSettingKeys.HighlightShortWindowSeconds, "abc");
        repository.Seed(AppSettingKeys.HighlightBaselineWindowSeconds, "999999");
        repository.Seed(AppSettingKeys.HighlightDensityMultiplier, "NaN");
        repository.Seed(AppSettingKeys.HighlightMinShortWindowCount, "-3");
        repository.Seed(AppSettingKeys.HighlightCooldownSeconds, "999999");

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(10, options.ShortWindowSeconds);
        Assert.Equal(3600, options.BaselineWindowSeconds);
        Assert.Equal(3.0, options.DensityMultiplier, 6);
        Assert.Equal(1, options.MinShortWindowCount);
        Assert.Equal(86_400, options.CooldownSeconds);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("2,5")]
    [InlineData("not-a-number")]
    public async Task LoadAsync_NonFiniteOrMalformedMultiplier_FallsBackToDefault(string value)
    {
        var repository = new InMemoryAppSettingsRepository();
        repository.Seed(AppSettingKeys.HighlightDensityMultiplier, value);

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(3.0, options.DensityMultiplier, 6);
    }

    [Fact]
    public async Task LoadAsync_BaselineBelowShortWindow_IsClampedToShortWindow()
    {
        var repository = new InMemoryAppSettingsRepository();
        repository.Seed(AppSettingKeys.HighlightShortWindowSeconds, "300");
        repository.Seed(AppSettingKeys.HighlightBaselineWindowSeconds, "60");

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(300, options.ShortWindowSeconds);
        Assert.Equal(300, options.BaselineWindowSeconds);
    }

    [Fact]
    public async Task LoadAsync_MissingKeysKeepDefaults()
    {
        var repository = new InMemoryAppSettingsRepository();
        repository.Seed(AppSettingKeys.HighlightShortWindowSeconds, "20");

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Equal(20, options.ShortWindowSeconds);
        Assert.Equal(180, options.BaselineWindowSeconds);
        Assert.Equal(3.0, options.DensityMultiplier, 6);
        Assert.Equal(15, options.MinShortWindowCount);
        Assert.Equal(60, options.CooldownSeconds);
        Assert.Equal(HighlightRuleOptions.DefaultBoostKeywords.ToArray(), options.BoostKeywords.ToArray());
    }

    [Fact]
    public async Task LoadAsync_EmptyKeywordsValue_ReturnsEmptyList()
    {
        var repository = new InMemoryAppSettingsRepository();
        repository.Seed(AppSettingKeys.HighlightBoostKeywords, "  \r\n  ");

        var options = await HighlightSettingsStore.LoadAsync(repository);

        Assert.Empty(options.BoostKeywords);
    }

    [Fact]
    public async Task SaveAsync_NormalizesBeforeWriting()
    {
        var repository = new InMemoryAppSettingsRepository();
        var options = new HighlightRuleOptions
        {
            ShortWindowSeconds = 0,
            BaselineWindowSeconds = 0,
            DensityMultiplier = double.NaN,
            MinShortWindowCount = 0,
            CooldownSeconds = -1,
            BoostKeywords = new[] { " 666 ", "草", "666", "" },
        };

        await HighlightSettingsStore.SaveAsync(repository, options);

        var snapshot = repository.Snapshot;
        Assert.Equal("1", snapshot[AppSettingKeys.HighlightShortWindowSeconds]);
        Assert.Equal("1", snapshot[AppSettingKeys.HighlightBaselineWindowSeconds]);
        Assert.Equal("3", snapshot[AppSettingKeys.HighlightDensityMultiplier]);
        Assert.Equal("1", snapshot[AppSettingKeys.HighlightMinShortWindowCount]);
        Assert.Equal("0", snapshot[AppSettingKeys.HighlightCooldownSeconds]);
        Assert.Equal("666\n草", snapshot[AppSettingKeys.HighlightBoostKeywords]);
    }

    [Theory]
    [InlineData("666\r\n 草 \n\nPOG\n666", new[] { "666", "草", "POG" })]
    [InlineData("Pog\n666\npog", new[] { "Pog", "666" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    public void ParseBoostKeywords_SplitsTrimsAndDeduplicates(string? value, string[] expected)
    {
        Assert.Equal(expected, HighlightSettingsStore.ParseBoostKeywords(value));
    }

    [Theory]
    [InlineData(new[] { " 666 ", "草", "  ", "666", "POG" }, "666\n草\nPOG")]
    [InlineData(new[] { "666", "草" }, "666\n草")]
    public void FormatBoostKeywords_TrimsAndJoinsWithNewline(string[] keywords, string expected)
    {
        Assert.Equal(expected, HighlightSettingsStore.FormatBoostKeywords(keywords));
    }

    [Fact]
    public void FormatBoostKeywords_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, HighlightSettingsStore.FormatBoostKeywords(null));
    }
}
