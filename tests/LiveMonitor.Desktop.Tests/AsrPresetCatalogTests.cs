using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>语音识别预设目录（`AsrPresetCatalog`）的直接单测。</summary>
public sealed class AsrPresetCatalogTests
{
    [Fact]
    public void Presets_KeepStableOrderAndCount()
    {
        Assert.Equal(
            new[] { "siliconflow_cn", "siliconflow_global", "groq", "custom" },
            AsrPresetCatalog.Presets.Select(preset => preset.Id));
    }

    [Fact]
    public void PresetIds_AreUniqueAndDefaultExists()
    {
        var ids = AsrPresetCatalog.Presets.Select(preset => preset.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(AsrPresetCatalog.DefaultPresetId, ids);
    }

    [Theory]
    [InlineData("siliconflow_cn", "硅基流动（国内）")]
    [InlineData("siliconflow_global", "硅基流动（国际）")]
    [InlineData("groq", "Groq")]
    [InlineData("custom", "自定义（OpenAI 兼容）")]
    public void Preset_CarriesExpectedDisplayName(string id, string displayName)
    {
        var preset = AsrPresetCatalog.Presets.Single(p => p.Id == id);

        Assert.Equal(displayName, preset.DisplayName);
    }

    [Theory]
    [InlineData("siliconflow_cn")]
    [InlineData("siliconflow_global")]
    [InlineData("groq")]
    public void BuiltInPresets_HaveEndpointModelAndDescription(string id)
    {
        var preset = AsrPresetCatalog.Presets.Single(p => p.Id == id);

        Assert.False(string.IsNullOrWhiteSpace(preset.Endpoint));
        Assert.False(string.IsNullOrWhiteSpace(preset.Model));
        Assert.False(string.IsNullOrWhiteSpace(preset.Description));
        Assert.StartsWith("https://", preset.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomPreset_LeavesEndpointAndModelForUserInput()
    {
        var preset = AsrPresetCatalog.Presets.Single(p => p.Id == "custom");

        Assert.Equal(string.Empty, preset.Endpoint);
        Assert.Equal(string.Empty, preset.Model);
        Assert.False(string.IsNullOrWhiteSpace(preset.Description));
    }

    [Fact]
    public void Descriptions_AreDistinct()
    {
        var descriptions = AsrPresetCatalog.Presets.Select(p => p.Description).ToArray();

        Assert.Equal(descriptions.Length, descriptions.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("groq", "groq")]
    [InlineData("  GROQ  ", "groq")]
    [InlineData("custom", "custom")]
    [InlineData("unknown", "siliconflow_cn")]
    [InlineData("", "siliconflow_cn")]
    [InlineData(null, "siliconflow_cn")]
    public void Resolve_NormalizesOrFallsBack(string? presetId, string expectedId)
    {
        Assert.Equal(expectedId, AsrPresetCatalog.Resolve(presetId).Id);
    }

    [Fact]
    public void Store_PresetsAndResolveDelegateToCatalog()
    {
        Assert.Same(AsrPresetCatalog.Presets, AsrSettingsStore.Presets);
        Assert.Equal("groq", AsrSettingsStore.ResolvePreset("GROQ").Id);
        Assert.Equal(AsrPresetCatalog.DefaultPresetId, AsrSettingsStore.DefaultPresetId);
    }
}
