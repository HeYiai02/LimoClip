using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class AdaptiveQualityPolicyTests
{
    [Theory]
    [InlineData(null, null, AdaptiveQualityPolicy.LoadLevel.Unknown)]
    [InlineData(40.0, 30.0, AdaptiveQualityPolicy.LoadLevel.Healthy)]
    [InlineData(60.0, 55.0, AdaptiveQualityPolicy.LoadLevel.Healthy)]
    [InlineData(61.0, 30.0, AdaptiveQualityPolicy.LoadLevel.Neutral)]
    [InlineData(40.0, 56.0, AdaptiveQualityPolicy.LoadLevel.Neutral)]
    [InlineData(85.0, 30.0, AdaptiveQualityPolicy.LoadLevel.Pressure)]
    [InlineData(40.0, 80.0, AdaptiveQualityPolicy.LoadLevel.Pressure)]
    [InlineData(95.0, 90.0, AdaptiveQualityPolicy.LoadLevel.Pressure)]
    public void Classify_MapsGpuSampleToLoadLevel(
        double? utilization,
        double? decoder,
        AdaptiveQualityPolicy.LoadLevel expected)
    {
        var level = AdaptiveQualityPolicy.Classify(new GpuLoadSnapshot(utilization, decoder));

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Classify_WithOnlyUtilizationSample_IgnoresMissingDecoder()
    {
        Assert.Equal(
            AdaptiveQualityPolicy.LoadLevel.Healthy,
            AdaptiveQualityPolicy.Classify(new GpuLoadSnapshot(45, null)));
        Assert.Equal(
            AdaptiveQualityPolicy.LoadLevel.Pressure,
            AdaptiveQualityPolicy.Classify(new GpuLoadSnapshot(null, 90)));
    }

    [Fact]
    public void GpuLoadSnapshot_ReportsWhetherSampleExists()
    {
        Assert.False(GpuLoadSnapshot.Empty.HasSample);
        Assert.True(new GpuLoadSnapshot(10, null).HasSample);
        Assert.True(new GpuLoadSnapshot(null, 10).HasSample);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void ShouldCompact_RequiresTwoPressureCycles(int pressureCycles, bool expected)
    {
        Assert.Equal(expected, AdaptiveQualityPolicy.ShouldCompact(pressureCycles));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ShouldDowngradeQuality_RequiresExtraPressureAfterCompact(int cycles, bool expected)
    {
        Assert.Equal(expected, AdaptiveQualityPolicy.ShouldDowngradeQuality(cycles));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    public void ShouldRecover_RequiresSustainedHealthyCycles(int healthyCycles, bool expected)
    {
        Assert.Equal(expected, AdaptiveQualityPolicy.ShouldRecover(healthyCycles));
    }

    [Fact]
    public void Thresholds_KeepPressureAboveHealthyToAvoidOscillation()
    {
        Assert.True(AdaptiveQualityPolicy.PressureUtilizationThreshold
            > AdaptiveQualityPolicy.HealthyUtilizationThreshold);
        Assert.True(AdaptiveQualityPolicy.PressureDecoderThreshold
            > AdaptiveQualityPolicy.HealthyDecoderThreshold);
        Assert.True(AdaptiveQualityPolicy.HealthyCyclesBeforeRecovery
            > AdaptiveQualityPolicy.PressureCyclesBeforeCompact);
    }
}
