using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>录制中心历史行展示规则（房间名回退、汇总文案、删除/剪辑入口与提示）。</summary>
public sealed class RecordingHistoryDisplayLogicTests
{
    [Theory]
    [InlineData(null, "123", "房间 123")]
    [InlineData("", "123", "房间 123")]
    [InlineData("   ", "123", "房间 123")]
    [InlineData("房间A", "123", "房间A")]
    public void RoomTitle_falls_back_to_room_id(string? displayName, string roomId, string expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.RoomTitle(displayName, roomId));
    }

    [Theory]
    [InlineData(RecordingStatus.Starting, true)]
    [InlineData(RecordingStatus.Recording, true)]
    [InlineData(RecordingStatus.Stopping, true)]
    [InlineData(RecordingStatus.Completed, false)]
    [InlineData(RecordingStatus.Failed, false)]
    public void IsActive_detects_running_statuses(RecordingStatus status, bool expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.IsActive(status));
        Assert.Equal(!expected, RecordingHistoryDisplayLogic.CanDelete(status));
        Assert.Equal(!expected, RecordingHistoryDisplayLogic.CanShowClip(status));
    }

    [Theory]
    [InlineData(RecordingStatus.Recording, "该录制仍在进行中，请先停止录制再删除记录")]
    [InlineData(RecordingStatus.Completed, "删除记录并同时删除对应的 .mkv 与 .jsonl 文件")]
    [InlineData(RecordingStatus.Failed, "删除记录并同时删除对应的 .mkv 与 .jsonl 文件")]
    public void DeleteTip_depends_on_active_status(RecordingStatus status, string expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.DeleteTip(status));
    }

    [Fact]
    public void Summary_joins_metrics()
    {
        Assert.Equal(
            "时长 12:34 · 大小 512.0 MB · 弹幕 42 条 · 高能 3",
            RecordingHistoryDisplayLogic.Summary("12:34", "512.0 MB", "42", "3"));
    }

    [Theory]
    [InlineData(RecordingStatus.Completed, true, true)]
    [InlineData(RecordingStatus.Failed, true, true)]
    [InlineData(RecordingStatus.Completed, false, false)]
    [InlineData(RecordingStatus.Recording, true, false)]
    [InlineData(RecordingStatus.Starting, false, false)]
    public void CanClip_requires_finished_status_and_existing_file(RecordingStatus status, bool fileExists, bool expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.CanClip(status, @"C:\rec\a.mkv", fileExists));
        Assert.False(RecordingHistoryDisplayLogic.CanClip(status, "  ", fileExists));
        Assert.False(RecordingHistoryDisplayLogic.CanClip(status, null, fileExists));
    }

    [Theory]
    [InlineData(RecordingStatus.Recording, true, "该录制仍在进行中，请先停止录制再进入剪辑台")]
    [InlineData(RecordingStatus.Completed, true, "进入剪辑台，预览/裁剪并导出 MP4")]
    [InlineData(RecordingStatus.Completed, false, "录像文件不存在，无法进入剪辑台")]
    [InlineData(RecordingStatus.Failed, false, "录像文件不存在，无法进入剪辑台")]
    public void ClipTip_explains_reason(RecordingStatus status, bool canClip, string expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.ClipTip(status, canClip));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData(@"C:\rec\a.mkv", "a.mkv")]
    [InlineData(@"C:\rec\nested\b.jsonl", "b.jsonl")]
    public void FileName_returns_leaf_name(string? path, string expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.FileName(path));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("boom", true)]
    public void HasError_detects_non_blank_message(string? message, bool expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.HasError(message));
    }

    [Theory]
    [InlineData(RecordingStatus.Failed, "合并失败", true)]
    [InlineData(RecordingStatus.Completed, "已降级合并", true)]
    [InlineData(RecordingStatus.Completed, null, false)]
    [InlineData(RecordingStatus.Completed, "   ", false)]
    [InlineData(RecordingStatus.Recording, "还在录", false)]
    public void CanRetryMerge_requires_finished_session_with_error(
        RecordingStatus status, string? errorMessage, bool expected)
    {
        Assert.Equal(expected, RecordingHistoryDisplayLogic.CanRetryMerge(status, errorMessage));
    }

    [Fact]
    public void RetryMergeTip_mentions_parts_and_recovery()
    {
        Assert.Contains(".part", RecordingHistoryDisplayLogic.RetryMergeTip);
        Assert.Contains("合并失败", RecordingHistoryDisplayLogic.RetryMergeTip);
    }

    [Fact]
    public void Constants_match_ui_copy()
    {
        Assert.Contains("边录边剪", RecordingHistoryDisplayLogic.LiveClipTip);
        Assert.Contains("仍在进行中", RecordingHistoryDisplayLogic.DeleteActiveTip);
        Assert.Contains("仍在进行中", RecordingHistoryDisplayLogic.ClipActiveTip);
        Assert.Contains("不存在", RecordingHistoryDisplayLogic.ClipMissingTip);
    }
}
