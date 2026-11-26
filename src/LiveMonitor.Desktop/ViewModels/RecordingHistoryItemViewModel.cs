using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心「历史录像」列表的一行：开始时间、房间快照名、状态、时长、大小、
/// 弹幕数、高能数与错误信息；提供打开文件夹和删除记录操作。
/// </summary>
public sealed partial class RecordingHistoryItemViewModel : ObservableObject
{
    private readonly RecordingHistoryViewModel _history;

    public RecordingHistoryItemViewModel(RecordingSession session, RecordingHistoryViewModel history)
    {
        Session = session;
        _history = history;
    }

    public RecordingSession Session { get; }

    public Guid SessionId => Session.Id;

    public string RoomTitle => string.IsNullOrWhiteSpace(Session.RoomDisplayName)
        ? $"房间 {Session.RoomId}"
        : Session.RoomDisplayName;

    public string PlatformBadge => RecordingUiFormat.PlatformBadge(Session.Platform);

    public string PlatformBadgeBackground => RecordingUiFormat.PlatformBadgeBackground(Session.Platform);

    public string PlatformBadgeForeground => RecordingUiFormat.PlatformBadgeForeground(Session.Platform);

    public string StartedAtText => RecordingUiFormat.FormatStartedAt(Session.StartedAt);

    public string StatusText => RecordingUiFormat.StatusText(Session.Status);

    public string StatusBrush => RecordingUiFormat.StatusBrush(Session.Status);

    public string DurationText => RecordingUiFormat.FormatDuration(Session.DurationSeconds);

    public string FileSizeText => RecordingUiFormat.FormatBytes(Session.FileSizeBytes);

    public string DanmakuCountText => Session.DanmakuCount.ToString();

    public string HighlightCountText => Session.HighlightCount.ToString();

    public string Summary
        => RecordingHistoryDisplayLogic.Summary(DurationText, FileSizeText, DanmakuCountText, HighlightCountText);

    public bool HasError => RecordingHistoryDisplayLogic.HasError(Session.ErrorMessage);

    public string ErrorMessage => Session.ErrorMessage ?? string.Empty;

    public string VideoFileName => RecordingHistoryDisplayLogic.FileName(Session.VideoFilePath);

    public string DanmakuFileName => RecordingHistoryDisplayLogic.FileName(Session.DanmakuFilePath);

    public string VideoFilePath => Session.VideoFilePath;

    /// <summary>正在录制的会话不允许删除（RecordingManager 也会拒绝）。</summary>
    public bool IsActive => RecordingHistoryDisplayLogic.IsActive(Session.Status);

    public bool CanDelete => RecordingHistoryDisplayLogic.CanDelete(Session.Status);

    public string DeleteTip => RecordingHistoryDisplayLogic.DeleteTip(Session.Status);

    /// <summary>实时进度会更新活跃会话的时长与大小，删除按钮仍保持禁用。</summary>
    public void ApplyProgress(RecordingProgress progress)
    {
        Session.DurationSeconds = progress.Duration.TotalSeconds;
        Session.FileSizeBytes = progress.FileSizeBytes;
        Session.DanmakuCount = progress.DanmakuCount;
        Session.HighlightCount = progress.HighlightCount;

        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DanmakuCountText));
        OnPropertyChanged(nameof(HighlightCountText));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>V0.4：录制已结束且 .mkv 仍在磁盘上时才能进入剪辑台。</summary>
    public bool CanClip
        => RecordingHistoryDisplayLogic.CanClip(Session.Status, Session.VideoFilePath, File.Exists(Session.VideoFilePath));

    /// <summary>
    /// 录制进行中时历史行不显示「去剪辑」（文件尚未合并完成），改为显示「✂ 边录边剪」入口。
    /// </summary>
    public bool IsLive => IsActive;

    /// <summary>历史行「去剪辑」按钮的可见性：录制中的行由边录边剪入口替代。</summary>
    public bool CanShowClip => RecordingHistoryDisplayLogic.CanShowClip(Session.Status);

    public string LiveClipTip => RecordingHistoryDisplayLogic.LiveClipTip;

    public string ClipTip => RecordingHistoryDisplayLogic.ClipTip(Session.Status, CanClip);

    [RelayCommand]
    private Task OpenFolderAsync() => _history.OpenFolderAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _history.RequestDeleteAsync(this);

    [RelayCommand]
    private Task ClipAsync() => _history.RequestOpenClipAsync(this);

    /// <summary>边录边剪：把该场次正在写入的分片直接送进剪辑台实时剪辑。</summary>
    [RelayCommand]
    private Task LiveClipAsync()
        => Session.LiveRoomId is { } liveRoomId
            ? _history.RequestOpenLiveClipAsync(liveRoomId)
            : Task.CompletedTask;
}
