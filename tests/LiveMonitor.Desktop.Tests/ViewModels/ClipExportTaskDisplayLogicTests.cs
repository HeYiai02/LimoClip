using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipExportTaskDisplayLogicTests
{
    [Fact]
    public void FileName_AppendsMp4Extension()
    {
        Assert.Equal("clip_001.mp4", ClipExportTaskDisplayLogic.FileName("clip_001"));
    }

    [Theory]
    [InlineData(ClipExportMode.CleanOnly, "无弹幕")]
    [InlineData(ClipExportMode.BurnedDanmaku, "弹幕烧录")]
    [InlineData(ClipExportMode.Both, "双版")]
    public void ModeText_MapsModes(ClipExportMode mode, string expected)
    {
        Assert.Equal(expected, ClipExportTaskDisplayLogic.ModeText(mode));
    }

    [Theory]
    [InlineData(0, 65, "00:00 - 01:05")]
    [InlineData(65, 3600, "01:05 - 1:00:00")]
    [InlineData(-5, 125, "00:00 - 02:05")]
    public void RangeText_UsesSecondPrecision(int startSeconds, int endSeconds, string expected)
    {
        var text = ClipExportTaskDisplayLogic.RangeText(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(1.5, "1.5x")]
    [InlineData(2.06, "2.1x")]
    [InlineData(0.34, "0.3x")]
    public void SpeedText_FormatsOneDecimalOrEmpty(double? speed, string expected)
    {
        Assert.Equal(expected, ClipExportTaskDisplayLogic.SpeedText(speed));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(42.5, 42.5)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void ClampProgress_ClampsToZeroHundred(double input, double expected)
    {
        Assert.Equal(expected, ClipExportTaskDisplayLogic.ClampProgress(input));
    }

    [Fact]
    public void BuildOutputText_JoinsFileNames()
    {
        var text = ClipExportTaskDisplayLogic.BuildOutputText(new[]
        {
            @"C:\out\clip_001_clean.mp4",
            @"C:\out\clip_001_danmaku.mp4"
        });

        Assert.Equal("clip_001_clean.mp4、clip_001_danmaku.mp4", text);
    }

    [Fact]
    public void BuildOutputText_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClipExportTaskDisplayLogic.BuildOutputText(null));
        Assert.Equal(string.Empty, ClipExportTaskDisplayLogic.BuildOutputText(Array.Empty<string>()));
    }

    [Fact]
    public void StateConstants_MatchUiCopy()
    {
        Assert.Equal("等待导出", ClipExportTaskDisplayLogic.WaitingState);
        Assert.Equal("准备导出…", ClipExportTaskDisplayLogic.PreparingState);
        Assert.Equal("导出完成", ClipExportTaskDisplayLogic.CompletedState);
        Assert.Equal("已取消", ClipExportTaskDisplayLogic.CanceledState);
        Assert.Equal("导出失败", ClipExportTaskDisplayLogic.FailedState);
        Assert.Equal(ClipExportTaskDisplayLogic.EmptySpeedText, string.Empty);
        Assert.Equal(ClipExportTaskDisplayLogic.MaxProgress, 100d);
    }

    [Fact]
    public void ClipExportTaskViewModel_DelegatesDisplayProperties()
    {
        var request = new ClipExportRequest(
            InputVideoPath: @"C:\in\recording.mkv",
            CompanionDanmakuPath: null,
            OutputDirectory: @"C:\out",
            OutputBaseName: "clip_001",
            StartTime: TimeSpan.FromSeconds(65),
            EndTime: TimeSpan.FromSeconds(125),
            Mode: ClipExportMode.BurnedDanmaku);
        var task = new ClipExportTaskViewModel(request, null, "h264", new FakeClipExporter());

        Assert.Equal("clip_001.mp4", task.FileName);
        Assert.Equal("弹幕烧录", task.ModeText);
        Assert.Equal("01:05 - 02:05", task.RangeText);
        Assert.Equal("等待导出", task.StateText);
    }

    private sealed class FakeClipExporter : IClipExporter
    {
        public Task<IReadOnlyList<string>> ExportAsync(
            ClipExportRequest request,
            IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
            string sourceVideoCodec,
            IProgress<ClipExportProgress>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
