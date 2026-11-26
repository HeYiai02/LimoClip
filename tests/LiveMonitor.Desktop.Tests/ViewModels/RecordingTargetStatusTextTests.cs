using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class RecordingTargetStatusTextTests
{
    [Fact]
    public void AutoRecordChanged_includes_state()
    {
        Assert.Equal("「房间A」自动录制已开启。", RecordingTargetStatusText.AutoRecordChanged("房间A", true));
        Assert.Equal("「房间A」自动录制已关闭。", RecordingTargetStatusText.AutoRecordChanged("房间A", false));
    }

    [Fact]
    public void AutoRecordUpdateFailed_includes_title_and_message()
        => Assert.Equal("更新自动录制失败（房间A）：boom", RecordingTargetStatusText.AutoRecordUpdateFailed("房间A", "boom"));

    [Fact]
    public void RecordingStarting_includes_title()
        => Assert.Equal("正在启动录制「房间A」…", RecordingTargetStatusText.RecordingStarting("房间A"));

    [Fact]
    public void RecordingStarted_includes_title_and_path()
        => Assert.Equal("「房间A」已开始录制，文件将写入 D:\\clips\\a.mkv", RecordingTargetStatusText.RecordingStarted("房间A", "D:\\clips\\a.mkv"));

    [Fact]
    public void RecordingStartFailed_includes_title_and_message()
        => Assert.Equal("开录失败（房间A）：boom", RecordingTargetStatusText.RecordingStartFailed("房间A", "boom"));

    [Fact]
    public void RecordingStopping_includes_title()
        => Assert.Equal("正在停止录制「房间A」…", RecordingTargetStatusText.RecordingStopping("房间A"));

    [Fact]
    public void RecordingStopped_includes_title()
        => Assert.Equal("「房间A」录制已停止。", RecordingTargetStatusText.RecordingStopped("房间A"));

    [Fact]
    public void RecordingStopFailed_includes_title_and_message()
        => Assert.Equal("停止录制失败（房间A）：boom", RecordingTargetStatusText.RecordingStopFailed("房间A", "boom"));
}
