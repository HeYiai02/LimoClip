using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class GpuMetricsLogicTests
{
    [Theory]
    [InlineData("42", 42d)]
    [InlineData(" 42 ", 42d)]
    [InlineData("3.5", 3.5d)]
    [InlineData("0", 0d)]
    public void ParseNumber_ParsesInvariantNumeric(string text, double expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.ParseNumber(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("[N/A]")]
    [InlineData("abc")]
    public void ParseNumber_MissingOrInvalidIsNull(string? text)
    {
        Assert.Null(GpuMetricsLogic.ParseNumber(text));
    }

    [Fact]
    public void ParseSample_ReadsAllFourColumnsFromFirstLine()
    {
        var sample = GpuMetricsLogic.ParseSample("32, 12, 0, 5123");

        Assert.NotNull(sample);
        Assert.Equal(32d, sample.Value.Utilization);
        Assert.Equal(12d, sample.Value.Decoder);
        Assert.Equal(0d, sample.Value.Encoder);
        Assert.Equal(5123d, sample.Value.MemoryMb);
    }

    [Fact]
    public void ParseSample_HandlesNaFieldsAndSpaces()
    {
        var sample = GpuMetricsLogic.ParseSample("  [N/A] , 12 , N/A , 5123  ");

        Assert.NotNull(sample);
        Assert.Null(sample.Value.Utilization);
        Assert.Equal(12d, sample.Value.Decoder);
        Assert.Null(sample.Value.Encoder);
        Assert.Equal(5123d, sample.Value.MemoryMb);
    }

    [Fact]
    public void ParseSample_IgnoresBlankLinesAndTakesFirstDataLine()
    {
        var sample = GpuMetricsLogic.ParseSample("\r\n\r\n10, 20, 30, 40\r\n50, 60, 70, 80\r\n");

        Assert.NotNull(sample);
        Assert.Equal(10d, sample.Value.Utilization);
        Assert.Equal(40d, sample.Value.MemoryMb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("32, 12, 0")]
    [InlineData("32")]
    public void ParseSample_InvalidOutputIsNull(string output)
    {
        Assert.Null(GpuMetricsLogic.ParseSample(output));
    }

    [Fact]
    public void ParseSample_NullOutputIsNull()
    {
        Assert.Null(GpuMetricsLogic.ParseSample(null));
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(734003200L, "700 MB")]
    public void MemoryText_FormatsWholeMegabytes(long bytes, string expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.MemoryText(bytes));
    }

    [Theory]
    [InlineData(75d, "GPU 75%")]
    [InlineData(0d, "GPU 0%")]
    [InlineData(99.6d, "GPU 100%")]
    public void UtilizationText_FormatsPercentage(double value, string expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.UtilizationText(value));
    }

    [Fact]
    public void UtilizationText_NullShowsPlaceholder()
    {
        Assert.Equal("GPU --", GpuMetricsLogic.UtilizationText(null));
    }

    [Theory]
    [InlineData(12d, "解码 12%")]
    [InlineData(null, "解码 --")]
    public void DecoderText_FormatsOrPlaceholder(double? value, string expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.DecoderText(value));
    }

    [Theory]
    [InlineData(7d, "编码 7%")]
    [InlineData(null, "编码 --")]
    public void EncoderText_FormatsOrPlaceholder(double? value, string expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.EncoderText(value));
    }

    [Theory]
    [InlineData(5123d, "显存 5123 MB")]
    [InlineData(null, "")]
    public void GpuMemoryText_FormatsOrEmpty(double? value, string expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.GpuMemoryText(value));
    }

    [Theory]
    [InlineData(50d, 50d)]
    [InlineData(120d, 100d)]
    [InlineData(-5d, 0d)]
    [InlineData(null, 0d)]
    public void BarValue_ClampsToPercentageRange(double? value, double expected)
    {
        Assert.Equal(expected, GpuMetricsLogic.BarValue(value));
    }
}
