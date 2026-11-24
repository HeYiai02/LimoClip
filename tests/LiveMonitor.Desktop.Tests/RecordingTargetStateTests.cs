using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RecordingTargetStateTests
{
    /// <summary>
    /// 停止录制时 RecordingManager 先发 SessionCompleted、再发一次最终 Progress；
    /// 后者晚到不能把行内 IsRecording 拉回 true，否则「停止录制」按钮要再点一次才恢复成「立即开录」。
    /// </summary>
    [Fact]
    public async Task ProgressAfterSessionCompleted_DoesNotResurrectRecordingState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();

            var room = new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = 1,
                Platform = PlatformType.Bilibili,
                RoomId = "7734200",
                RawUrl = "https://live.bilibili.com/7734200",
                Alias = "测试房间",
                CreatedAt = DateTime.UtcNow
            };
            var item = new RecordingTargetItemViewModel(room, target: null, viewModel.RecordingCenter);
            Assert.False(item.IsRecording);
            Assert.Equal("立即开录", item.StartStopText);

            var session = new RecordingSession
            {
                Id = Guid.NewGuid(),
                LiveRoomId = room.Id,
                Platform = room.Platform,
                RoomId = room.RoomId,
                RoomDisplayName = item.DisplayTitle,
                Status = RecordingStatus.Completed,
                DurationSeconds = 12,
                FileSizeBytes = 2048,
                DanmakuCount = 3,
                HighlightCount = 1
            };

            item.ApplySessionCompleted(session);
            item.ApplyProgress(new RecordingProgress(
                session.Id,
                TimeSpan.FromSeconds(12),
                session.FileSizeBytes,
                2500d,
                session.DanmakuCount,
                session.HighlightCount));

            Assert.False(item.IsRecording);
            Assert.Equal("立即开录", item.StartStopText);
            // 最终指标仍应被这次收尾进度更新。
            Assert.Equal("00:12", item.DurationText);
            Assert.Equal("3", item.DanmakuCountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
