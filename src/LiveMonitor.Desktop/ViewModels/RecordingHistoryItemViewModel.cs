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
    private readonly RecordingCenterViewModel _parent;

    public RecordingHistoryItemViewModel(RecordingSession session, RecordingCenterViewModel parent)
    {
        Session = session;
        _parent = parent;
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

    public string Summary =>
        $"时长 {DurationText} · 大小 {FileSizeText} · 弹幕 {DanmakuCountText} 条 · 高能 {HighlightCountText}";

    public bool HasError => !string.IsNullOrWhiteSpace(Session.ErrorMessage);

    public string ErrorMessage => Session.ErrorMessage ?? string.Empty;

    public string VideoFileName => Path.GetFileName(Session.VideoFilePath);

    public string DanmakuFileName => Path.GetFileName(Session.DanmakuFilePath);

    public string VideoFilePath => Session.VideoFilePath;

    /// <summary>正在录制的会话不允许删除（RecordingManager 也会拒绝）。</summary>
    public bool IsActive => Session.Status is RecordingStatus.Starting
        or RecordingStatus.Recording
        or RecordingStatus.Stopping;

    public bool CanDelete => !IsActive;

    public string DeleteTip => IsActive
        ? "该录制仍在进行中，请先停止录制再删除记录"
        : "删除记录并同时删除对应的 .mkv 与 .jsonl 文件";

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

    [RelayCommand]
    private Task OpenFolderAsync() => _parent.OpenHistoryFolderAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _parent.RequestDeleteAsync(this);
}
