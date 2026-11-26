using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 录制中心历史行入口契约：录制中的会话改为提供「边录边剪」入口，
/// 已结束的会话继续提供「去剪辑」入口。
/// </summary>
public sealed class RecordingHistoryItemViewModelTests
{
    [Theory]
    [InlineData(RecordingStatus.Starting)]
    [InlineData(RecordingStatus.Recording)]
    [InlineData(RecordingStatus.Stopping)]
    public void Active_session_exposes_live_clip_entry(RecordingStatus status)
    {
        var session = new RecordingSession
        {
            Status = status,
            LiveRoomId = Guid.NewGuid(),
            VideoFilePath = Path.Combine(Path.GetTempPath(), "not-yet-merged.mkv"),
        };

        var item = new RecordingHistoryItemViewModel(session, history: null!);

        Assert.True(item.IsLive);
        Assert.False(item.CanShowClip);
        Assert.False(item.CanClip);
        Assert.NotNull(item.LiveClipCommand);
        Assert.Contains("边录边剪", item.LiveClipTip);
    }

    [Fact]
    public void Finished_session_exposes_regular_clip_entry()
    {
        var session = new RecordingSession
        {
            Status = RecordingStatus.Completed,
            LiveRoomId = Guid.NewGuid(),
            VideoFilePath = Path.Combine(Path.GetTempPath(), "finished.mkv"),
        };

        var item = new RecordingHistoryItemViewModel(session, history: null!);

        Assert.False(item.IsLive);
        Assert.True(item.CanShowClip);
    }
}
