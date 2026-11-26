using System;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipStudioStatusTextTests
{
    [Fact]
    public void Constants_MatchExistingWording()
    {
        Assert.Equal("自动探测中…", ClipStudioStatusText.EncoderAutoDetecting);
        Assert.Equal("正在读取录制中的分片…", ClipStudioStatusText.LiveOpening);
        Assert.Equal("正在录制中，可对已录制部分进行剪辑。", ClipStudioStatusText.LiveOpened);
        Assert.Equal("录制已结束，已切换为离线剪辑模式。", ClipStudioStatusText.LiveCompleted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void LiveSegmented_IncludesPartNumber(int partNumber)
    {
        var text = ClipStudioStatusText.LiveSegmented(partNumber);

        Assert.Equal($"本次录制已发生自动分段（第 {partNumber} 段），边录边剪暂不可用，请等待录制结束后再剪辑。", text);
    }

    [Fact]
    public void LiveSyncStoppedBySegment_IncludesCurrentPartNumber()
    {
        Assert.Equal(
            "本次录制已发生自动分段（当前第 2 段），边录边剪已停止，请等待录制结束后再剪辑。",
            ClipStudioStatusText.LiveSyncStoppedBySegment(2));
    }

    [Theory]
    [InlineData(@"C:\recordings\part001.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void LivePartMissing_IncludesPath(string? partPath)
    {
        Assert.Equal($"录制分片尚未生成，请稍后再试：{partPath}", ClipStudioStatusText.LivePartMissing(partPath));
    }

    [Fact]
    public void LiveOpenFailed_IncludesMessage()
    {
        Assert.Equal("打开录制中分片失败：磁盘繁忙", ClipStudioStatusText.LiveOpenFailed("磁盘繁忙"));
    }

    [Theory]
    [InlineData(0, 0, "已同步最新录制进度（已录 00:00，安全可剪至 00:00）")]
    [InlineData(65, 60, "已同步最新录制进度（已录 01:05，安全可剪至 01:00）")]
    [InlineData(3665, 3660, "已同步最新录制进度（已录 01:01:05，安全可剪至 01:01:00）")]
    [InlineData(36000, 35995, "已同步最新录制进度（已录 10:00:00，安全可剪至 09:59:55）")]
    public void LiveSyncSucceeded_FormatsDurations(int recordedSeconds, int safeSeconds, string expected)
    {
        var text = ClipStudioStatusText.LiveSyncSucceeded(
            TimeSpan.FromSeconds(recordedSeconds),
            TimeSpan.FromSeconds(safeSeconds));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void FailureTexts_IncludeMessage()
    {
        Assert.Equal("同步录制进度失败：超时", ClipStudioStatusText.LiveSyncFailed("超时"));
        Assert.Equal("自动同步录制进度失败：超时", ClipStudioStatusText.AutoSyncFailed("超时"));
        Assert.Equal("切换离线剪辑失败：文件被占用", ClipStudioStatusText.LiveSwitchToOfflineFailed("文件被占用"));
        Assert.Equal(
            "增量读取弹幕失败（不影响导出）：编码错误",
            ClipStudioStatusText.LiveDanmakuLoadFailed("编码错误"));
        Assert.Equal(
            "边录边剪波形提取失败（不影响导出）：ffmpeg 退出",
            ClipStudioStatusText.LiveWaveformFailed("ffmpeg 退出"));
        Assert.Equal(
            "硬件编码器探测失败，已回退软件编码：无 GPU",
            ClipStudioStatusText.EncoderDetectionFailed("无 GPU"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("磁盘错误")]
    public void LiveCompletedWithoutFile_IncludesError(string? error)
    {
        Assert.Equal($"录制已结束，但录像文件不可用：{error}", ClipStudioStatusText.LiveCompletedWithoutFile(error));
    }

    [Theory]
    [InlineData("h264_nvenc", "NVENC · NVIDIA 硬件编码")]
    [InlineData("h264_qsv", "QSV · Intel 硬件编码")]
    [InlineData("h264_amf", "AMF · AMD 硬件编码")]
    [InlineData("libx264", "libx264 · 软件编码")]
    [InlineData("", "libx264 · 软件编码")]
    [InlineData(null, "libx264 · 软件编码")]
    public void EncoderLabel_MapsDetectorNames(string? encoderName, string expected)
    {
        Assert.Equal(expected, ClipStudioStatusText.EncoderLabel(encoderName));
    }
}
