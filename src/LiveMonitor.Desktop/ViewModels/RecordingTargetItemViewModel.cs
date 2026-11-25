using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心「录制目标」列表的一行：直播间信息、自动录制开关、独立保存目录，
/// 以及 RecordingManager 事件推送的实时指标（时长/码率/大小/弹幕数/高能数）。
/// </summary>
public sealed partial class RecordingTargetItemViewModel : ObservableObject
{
    private readonly RecordingCenterViewModel _parent;
    private readonly RecordingManager _recordings;
    private bool _suppressAutoRecordSave;
    private bool _disposed;
    private bool _autoRecordEnabled;
    private bool _isRecording;
    private bool _isLive;
    private bool _isBusy;
    private string? _customSaveDirectory;
    private string _statusText = "未录制";
    private string _statusBrush = "#5B6472";
    private string _durationText = "--";
    private string _bitrateText = "--";
    private string _fileSizeText = "--";
    private string _danmakuCountText = "0";
    private string _highlightCountText = "0";

    public RecordingTargetItemViewModel(
        LiveRoom room,
        RecordingTarget? target,
        RecordingCenterViewModel parent)
    {
        Room = room;
        _parent = parent;
        _recordings = parent.Recordings;
        _customSaveDirectory = string.IsNullOrWhiteSpace(target?.CustomSaveDirectory)
            ? null
            : target!.CustomSaveDirectory!.Trim();
        _autoRecordEnabled = target?.AutoRecordEnabled ?? false;
        RefreshFromManager();
    }

    public LiveRoom Room { get; }

    public Guid RoomId => Room.Id;

    /// <summary>「#序号 主播名」，与主库侧栏、录像文件名保持同一套命名。</summary>
    public string DisplayTitle => RecordingManager.FormatRoomDisplayName(Room);

    public string PlatformBadge => RecordingUiFormat.PlatformBadge(Room.Platform);

    public string PlatformBadgeBackground => RecordingUiFormat.PlatformBadgeBackground(Room.Platform);

    public string PlatformBadgeForeground => RecordingUiFormat.PlatformBadgeForeground(Room.Platform);

    public string RoomMeta => $"{RecordingUiFormat.PlatformName(Room.Platform)} · 房间号 {Room.RoomId}";

    public string? CustomSaveDirectory => _customSaveDirectory;

    public bool HasCustomDirectory => !string.IsNullOrWhiteSpace(_customSaveDirectory);

    /// <summary>行内目录：有独立目录就显示独立目录，否则提示跟随全局。</summary>
    public string DirectoryText => HasCustomDirectory ? _customSaveDirectory! : "跟随全局目录";

    public string DirectoryTip => HasCustomDirectory ? _customSaveDirectory! : _parent.GlobalDirectory;

    /// <summary>自定义清流没有开播查询接口，自动录制开关不适用（只支持手动开录）。</summary>
    public bool SupportsAutoRecord => Room.Platform != PlatformType.CustomStream;

    public bool AutoRecordEnabled
    {
        get => _autoRecordEnabled;
        set
        {
            if (value && !SupportsAutoRecord)
            {
                return;
            }

            if (!SetProperty(ref _autoRecordEnabled, value) || _suppressAutoRecordSave || _disposed)
            {
                return;
            }

            _ = SaveAutoRecordAsync(value);
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!SetProperty(ref _isRecording, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StartStopText));
            OnPropertyChanged(nameof(StartStopBackground));
            OnPropertyChanged(nameof(StartStopForeground));
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(ShowLiveBadge));
        }
    }

    /// <summary>
    /// 主库轮询得到的开播状态（true=直播中）。仅用于列表排序与卡片上的「直播中」徽章，
    /// 不参与录制状态机；未知状态按未开播处理。
    /// </summary>
    public bool IsLive => _isLive;

    /// <summary>卡片上是否显示「直播中」徽章：正在录制时由红色竖条/状态胶囊表达，不再重复显示。</summary>
    public bool ShowLiveBadge => _isLive && !_isRecording;

    /// <summary>由父 VM 用主库已知开播状态回填；返回状态是否发生变化。</summary>
    internal bool ApplyLiveStatus(bool? isLive)
    {
        if (_disposed)
        {
            return false;
        }

        var live = isLive == true;
        if (!SetProperty(ref _isLive, live, nameof(IsLive)))
        {
            return false;
        }

        OnPropertyChanged(nameof(ShowLiveBadge));
        return true;
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(StartStopText));
        }
    }

    public bool CanOperate => !IsBusy;

    public string StartStopText => IsBusy ? "处理中…" : IsRecording ? "停止录制" : "立即开录";

    public string StartStopBackground => IsRecording ? "#3B1D22" : "#1D3A5F";

    public string StartStopForeground => IsRecording ? "#FCA5A5" : "#BFDBFE";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string BitrateText
    {
        get => _bitrateText;
        private set => SetProperty(ref _bitrateText, value);
    }

    public string FileSizeText
    {
        get => _fileSizeText;
        private set => SetProperty(ref _fileSizeText, value);
    }

    public string DanmakuCountText
    {
        get => _danmakuCountText;
        private set => SetProperty(ref _danmakuCountText, value);
    }

    public string HighlightCountText
    {
        get => _highlightCountText;
        private set => SetProperty(ref _highlightCountText, value);
    }

    /// <summary>
    /// 用户在行内切换「自动录制」：持久化到 RecordingTargets；失败时回滚开关并显示原因。
    /// </summary>
    private async Task SaveAutoRecordAsync(bool enabled)
    {
        try
        {
            await _recordings
                .SetAutoRecordAsync(Room.Id, enabled, _customSaveDirectory)
                .ConfigureAwait(true);

            if (!IsRecording)
            {
                SetStatus(
                    enabled ? "待开播（自动录制）" : "未录制",
                    enabled ? "#FBBF24" : "#5B6472");
            }

            _parent.ReportStatus($"「{DisplayTitle}」自动录制已{(enabled ? "开启" : "关闭")}。");
            _parent.NotifyRecordingStateChanged();
        }
        catch (Exception ex)
        {
            _suppressAutoRecordSave = true;
            try
            {
                AutoRecordEnabled = !enabled;
            }
            finally
            {
                _suppressAutoRecordSave = false;
            }

            _parent.ReportStatus($"更新自动录制失败（{DisplayTitle}）：{ex.Message}");
        }
    }

    /// <summary>立即开录 / 停止录制。</summary>
    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        if (IsRecording)
        {
            await StopAsync().ConfigureAwait(true);
        }
        else
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    /// <summary>边录边剪：把该直播间正在写入的分片直接送进剪辑台实时剪辑。</summary>
    [RelayCommand]
    private Task LiveClipAsync() => _parent.RequestOpenLiveClipAsync(Room.Id);

    [RelayCommand]
    private Task ChooseDirectoryAsync() => _parent.ChooseRoomDirectoryAsync(this);

    [RelayCommand]
    private Task ClearDirectoryAsync() => _parent.ClearRoomDirectoryAsync(this);

    [RelayCommand]
    private Task OpenDirectoryAsync() => _parent.OpenTargetDirectoryAsync(this);

    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            _parent.ReportStatus($"正在启动录制「{DisplayTitle}」…");
            var session = await _recordings.StartAsync(Room).ConfigureAwait(true);
            ApplySessionStarted(session);
            _parent.ReportStatus($"「{DisplayTitle}」已开始录制，文件将写入 {session.VideoFilePath}");
            _parent.NotifyRecordingStateChanged();
            await _parent.RefreshHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _parent.ReportStatus($"开录失败（{DisplayTitle}）：{ex.Message}");
            RefreshFromManager();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            _parent.ReportStatus($"正在停止录制「{DisplayTitle}」…");
            await _recordings.StopAsync(Room.Id).ConfigureAwait(true);
            IsRecording = false;
            SetStatus("已停止", "#3DDC97");
            _parent.ReportStatus($"「{DisplayTitle}」录制已停止。");
            _parent.NotifyRecordingStateChanged();
            await _parent.RefreshHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _parent.ReportStatus($"停止录制失败（{DisplayTitle}）：{ex.Message}");
            RefreshFromManager();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>首次加载/刷新时按 RecordingManager 当前状态回填，避免列表显示与实际录制不一致。</summary>
    public void RefreshFromManager()
    {
        if (_disposed)
        {
            return;
        }

        var progress = _recordings.GetProgress(Room.Id);
        if (_recordings.IsRecording(Room.Id))
        {
            IsRecording = true;
            SetStatus("录制中", "#F87171");
            if (progress is not null)
            {
                ApplyProgressValues(progress);
            }
            return;
        }

        IsRecording = false;
        ResetMetrics();
        SetStatus(
            AutoRecordEnabled ? "待开播（自动录制）" : "未录制",
            AutoRecordEnabled ? "#FBBF24" : "#5B6472");
    }

    /// <summary>ProgressUpdated 事件（已由父 VM 切回 UI 线程）。</summary>
    public void ApplyProgress(RecordingProgress progress)
    {
        if (_disposed)
        {
            return;
        }

        // 停止录制时管理器会先发 SessionCompleted、后发一次最终 Progress；
        // 若这一帧进度晚于完成事件到达，不能把 IsRecording 拉回 true，否则按钮会卡在「停止录制」。
        if (!IsRecording && _recordings.IsRecording(Room.Id))
        {
            IsRecording = true;
            SetStatus("录制中", "#F87171");
        }

        ApplyProgressValues(progress);
    }

    /// <summary>SessionStarted 事件。</summary>
    public void ApplySessionStarted(RecordingSession session)
    {
        if (_disposed)
        {
            return;
        }

        IsRecording = true;
        SetStatus("录制中", "#F87171");
        DurationText = RecordingUiFormat.FormatDuration(0);
        BitrateText = "--";
        FileSizeText = RecordingUiFormat.FormatBytes(session.FileSizeBytes);
        DanmakuCountText = session.DanmakuCount.ToString();
        HighlightCountText = session.HighlightCount.ToString();
    }

    /// <summary>SessionCompleted 事件：保留本次录制的最终指标。</summary>
    public void ApplySessionCompleted(RecordingSession session)
    {
        if (_disposed)
        {
            return;
        }

        IsRecording = false;
        DurationText = RecordingUiFormat.FormatDuration(session.DurationSeconds);
        FileSizeText = RecordingUiFormat.FormatBytes(session.FileSizeBytes);
        BitrateText = "--";
        DanmakuCountText = session.DanmakuCount.ToString();
        HighlightCountText = session.HighlightCount.ToString();
        SetStatus(
            RecordingUiFormat.StatusText(session.Status),
            RecordingUiFormat.StatusBrush(session.Status));
    }

    /// <summary>修改独立保存目录后刷新行内展示（由父 VM 调用）。</summary>
    internal void ApplyCustomDirectory(string? directory)
    {
        _customSaveDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        OnPropertyChanged(nameof(CustomSaveDirectory));
        OnPropertyChanged(nameof(HasCustomDirectory));
        OnPropertyChanged(nameof(DirectoryText));
        OnPropertyChanged(nameof(DirectoryTip));
    }

    /// <summary>全局目录变化时刷新「跟随全局目录」的 tooltip。</summary>
    internal void NotifyGlobalDirectoryChanged()
    {
        OnPropertyChanged(nameof(DirectoryTip));
        OnPropertyChanged(nameof(DirectoryText));
    }

    public void Dispose() => _disposed = true;

    private void ApplyProgressValues(RecordingProgress progress)
    {
        DurationText = RecordingUiFormat.FormatDuration(progress.Duration);
        BitrateText = RecordingUiFormat.FormatBitrate(progress.BitrateKbps);
        FileSizeText = RecordingUiFormat.FormatBytes(progress.FileSizeBytes);
        DanmakuCountText = progress.DanmakuCount.ToString();
        HighlightCountText = progress.HighlightCount.ToString();
    }

    private void ResetMetrics()
    {
        DurationText = "--";
        BitrateText = "--";
        FileSizeText = "--";
        DanmakuCountText = "0";
        HighlightCountText = "0";
    }

    private void SetStatus(string text, string brush)
    {
        StatusText = text;
        StatusBrush = brush;
    }
}
