using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class ClipExportStatusTextTests
{
    [Theory]
    [InlineData(ClipExportStatusText.ExportButtonIdle, "开始导出 MP4")]
    [InlineData(ClipExportStatusText.ExportButtonBusy, "导出中…")]
    [InlineData(ClipExportStatusText.MissingInputs, "请先加载视频、设置有效入出点与输出目录。")]
    [InlineData(ClipExportStatusText.SafetyBufferBuilding, "安全缓冲生成中（已录不足 5 秒），请稍后再导出。")]
    [InlineData(ClipExportStatusText.InPointBeyondSafeRange, "当前入点已超过安全可剪范围，请把入点移到录制进度稍早的位置。")]
    [InlineData(ClipExportStatusText.SafetyBufferReserved, "已自动保留 5 秒安全缓冲避免影响录制。")]
    [InlineData(ClipExportStatusText.Canceled, "导出已取消。")]
    [InlineData(ClipExportStatusText.Canceling, "正在取消导出…")]
    [InlineData(ClipExportStatusText.DirectoryPickerMissing, "当前界面未接入目录选择器。")]
    [InlineData(ClipExportStatusText.OutputDirectoryMissing, "输出目录尚不存在，导出时会自动创建。")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void KeyframeTip_KeepsExpectedWording()
    {
        Assert.Equal(
            "无弹幕版使用 -c copy 无损切割，入点会自动对齐到最近关键帧，可能提前 1~3 秒。",
            ClipExportStatusText.KeyframeTip);
        Assert.Contains("-c copy", ClipExportStatusText.KeyframeTip);
    }

    [Fact]
    public void Started_JoinsRangeAndMode()
    {
        Assert.Equal("开始导出：00:05 - 00:15 · 无弹幕", ClipExportStatusText.Started("00:05 - 00:15", "无弹幕"));
    }

    [Fact]
    public void Completed_UsesOutputText()
    {
        Assert.Equal("导出完成：D:\\Clips\\a.mp4", ClipExportStatusText.Completed("D:\\Clips\\a.mp4"));
    }

    [Fact]
    public void Failed_UsesErrorMessage()
    {
        Assert.Equal("导出失败：ffmpeg 退出码 1", ClipExportStatusText.Failed("ffmpeg 退出码 1"));
    }

    [Fact]
    public void PickDirectoryFailed_PrependsContext()
    {
        Assert.Equal("选择导出目录失败：拒绝访问", ClipExportStatusText.PickDirectoryFailed("拒绝访问"));
    }

    [Fact]
    public void OpenDirectoryFailed_AppendsManualHint()
    {
        Assert.Equal(
            "打开输出目录失败：拒绝访问。请手动在资源管理器中打开：D:\\Clips",
            ClipExportStatusText.OpenDirectoryFailed("拒绝访问", "D:\\Clips"));
    }

    [Fact]
    public void ButtonTexts_AreDistinct()
    {
        Assert.NotEqual(ClipExportStatusText.ExportButtonIdle, ClipExportStatusText.ExportButtonBusy);
    }
}
