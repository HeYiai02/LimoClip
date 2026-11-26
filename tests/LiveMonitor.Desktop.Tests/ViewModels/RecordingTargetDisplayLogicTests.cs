using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class RecordingTargetDisplayLogicTests
{
    [Fact]
    public void IdleStatus_AutoRecordDisabled_ShowsNotRecording()
    {
        var status = RecordingTargetDisplayLogic.IdleStatus(autoRecordEnabled: false);

        Assert.Equal("未录制", status.Text);
        Assert.Equal("#5B6472", status.Brush);
    }

    [Fact]
    public void IdleStatus_AutoRecordEnabled_ShowsWaitingForLive()
    {
        var status = RecordingTargetDisplayLogic.IdleStatus(autoRecordEnabled: true);

        Assert.Equal("待开播（自动录制）", status.Text);
        Assert.Equal("#FBBF24", status.Brush);
    }

    [Fact]
    public void RecordingStatus_UsesRedCapsule()
    {
        var status = RecordingTargetDisplayLogic.RecordingStatus();

        Assert.Equal("录制中", status.Text);
        Assert.Equal("#F87171", status.Brush);
    }

    [Theory]
    [InlineData(RecordingStatus.Completed, "已完成")]
    [InlineData(RecordingStatus.Failed, "失败")]
    public void CompletedStatus_DelegatesToFormattingHelper(RecordingStatus status, string expectedText)
    {
        var display = RecordingTargetDisplayLogic.CompletedStatus(status);

        Assert.Equal(expectedText, display.Text);
        Assert.False(string.IsNullOrWhiteSpace(display.Brush));
    }

    [Theory]
    [InlineData(true, false, "处理中…")]
    [InlineData(false, false, "立即开录")]
    [InlineData(false, true, "停止录制")]
    [InlineData(true, true, "处理中…")]
    public void StartStopText_BusyDominatesRecordingState(bool isBusy, bool isRecording, string expected)
        => Assert.Equal(expected, RecordingTargetDisplayLogic.StartStopText(isBusy, isRecording));

    [Theory]
    [InlineData(true, "#3B1D22", "#FCA5A5")]
    [InlineData(false, "#1D3A5F", "#BFDBFE")]
    public void StartStopButton_UsesStatePalette(bool isRecording, string expectedBackground, string expectedForeground)
    {
        Assert.Equal(expectedBackground, RecordingTargetDisplayLogic.StartStopBackground(isRecording));
        Assert.Equal(expectedForeground, RecordingTargetDisplayLogic.StartStopForeground(isRecording));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void ShowLiveBadge_HidesWhileRecording(bool isLive, bool isRecording, bool expected)
        => Assert.Equal(expected, RecordingTargetDisplayLogic.ShowLiveBadge(isLive, isRecording));

    [Fact]
    public void CanOperate_BlockedWhileBusy()
    {
        Assert.True(RecordingTargetDisplayLogic.CanOperate(isBusy: false));
        Assert.False(RecordingTargetDisplayLogic.CanOperate(isBusy: true));
    }

    [Fact]
    public void Empty_HasPlaceholderCells()
    {
        var empty = RecordingTargetDisplayLogic.Empty;

        Assert.Equal("--", empty.Duration);
        Assert.Equal("--", empty.Bitrate);
        Assert.Equal("--", empty.FileSize);
        Assert.Equal("0", empty.DanmakuCount);
        Assert.Equal("0", empty.HighlightCount);
    }

    [Fact]
    public void FromProgress_FormatsAllCells()
    {
        var progress = new RecordingProgress(
            Guid.NewGuid(),
            TimeSpan.FromSeconds(75),
            FileSizeBytes: 2L * 1024 * 1024,
            BitrateKbps: 3500,
            DanmakuCount: 42,
            HighlightCount: 3);

        var metrics = RecordingTargetDisplayLogic.FromProgress(progress);

        Assert.Equal("01:15", metrics.Duration);
        Assert.Equal("2.0 MB", metrics.FileSize);
        Assert.Equal("42", metrics.DanmakuCount);
        Assert.Equal("3", metrics.HighlightCount);
        Assert.NotEqual("--", metrics.Bitrate);
    }

    [Fact]
    public void FromSessionStarted_UsesZeroDurationAndPendingBitrate()
    {
        var session = new RecordingSession
        {
            DurationSeconds = 120,
            FileSizeBytes = 4096,
            DanmakuCount = 7,
            HighlightCount = 1,
        };

        var metrics = RecordingTargetDisplayLogic.FromSessionStarted(session);

        Assert.Equal("00:00", metrics.Duration);
        Assert.Equal("--", metrics.Bitrate);
        Assert.Equal("7", metrics.DanmakuCount);
        Assert.Equal("1", metrics.HighlightCount);
    }

    [Fact]
    public void FromSessionCompleted_KeepsFinalMetricsWithoutBitrate()
    {
        var session = new RecordingSession
        {
            DurationSeconds = 125.4,
            FileSizeBytes = 3L * 1024 * 1024,
            DanmakuCount = 128,
            HighlightCount = 9,
        };

        var metrics = RecordingTargetDisplayLogic.FromSessionCompleted(session);

        Assert.Equal("02:05", metrics.Duration);
        Assert.Equal("--", metrics.Bitrate);
        Assert.Equal("3.0 MB", metrics.FileSize);
        Assert.Equal("128", metrics.DanmakuCount);
        Assert.Equal("9", metrics.HighlightCount);
    }
}
