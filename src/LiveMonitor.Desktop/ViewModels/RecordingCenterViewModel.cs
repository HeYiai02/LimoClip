using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// V0.3 录制中心：全局保存目录、按直播间的录制目标列表（自动录制/立即开录/实时指标）
/// 以及历史录像列表（打开文件夹/删除记录）。录制引擎在后台常驻，关闭本页不中断录制。
/// </summary>
public sealed partial class RecordingCenterViewModel : ObservableObject, IDisposable, IRecordingHistoryHost
{
    private readonly IRoomRepository _rooms;
    private readonly IRecordingTargetRepository _targets;
    private readonly IAppSettingsRepository _settings;
    private readonly RecordingManager _recordings;
    private readonly RecordingHistoryViewModel _history;
    private bool _initialized;
    private bool _disposed;
    private string _globalDirectory = RecordingManager.DefaultDirectory;
    private string _statusText = "就绪";
    private bool _isBusy;

    public RecordingCenterViewModel(
        IRoomRepository rooms,
        IRecordingTargetRepository targets,
        IRecordingSessionRepository sessions,
        IAppSettingsRepository settings,
        RecordingManager recordings)
    {
        _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        ArgumentNullException.ThrowIfNull(sessions);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        _history = new RecordingHistoryViewModel(sessions, this);
        // 历史子视图模型自己负责通知；录制中心保留同名只读转发属性（历史区绑定路径不变），
        // 因此需要把子视图模型的变化转发到本对象，避免绑定收不到通知。
        _history.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RecordingHistoryViewModel.IsEmpty))
            {
                OnPropertyChanged(nameof(IsHistoryEmpty));
            }
            else if (e.PropertyName is nameof(RecordingHistoryViewModel.Summary))
            {
                OnPropertyChanged(nameof(HistorySummary));
            }
        };
    }

    public ObservableCollection<RecordingTargetItemViewModel> Targets { get; } = new();

    /// <summary>历史录像列表（V0.7.5 起由 <see cref="RecordingHistoryViewModel"/> 持有，这里保持原绑定路径）。</summary>
    public ObservableCollection<RecordingHistoryItemViewModel> History => _history.Items;

    /// <summary>由 MainWindow 注入：删除历史录像前的确认弹窗，返回 true 才真正删除。</summary>
    public Func<RecordingHistoryItemViewModel, Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>由 MainWindow 注入：系统目录选择器，返回绝对路径；null 表示用户取消。</summary>
    public Func<Task<string?>>? PickDirectoryAsync { get; set; }

    /// <summary>V0.4：由 MainWindowViewModel 注入，录制中心请求把指定场次送进剪辑台。</summary>
    public Func<RecordingSession, Task>? OpenClipStudioAsync { get; set; }

    /// <summary>
    /// 由 MainWindowViewModel 注入：读取主库已知的直播间开播状态（true=直播中、false=未开播、null=未知）。
    /// 用于「录制中优先、直播中其次」的排序，以及卡片上的「直播中」徽章。
    /// </summary>
    public Func<Guid, bool?>? TryGetLiveStatus { get; set; }

    internal RecordingManager Recordings => _recordings;

    public string GlobalDirectory
    {
        get => _globalDirectory;
        private set
        {
            if (SetProperty(ref _globalDirectory, value))
            {
                foreach (var item in Targets)
                {
                    item.NotifyGlobalDirectoryChanged();
                }
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsTargetsEmpty => Targets.Count == 0;

    public bool IsHistoryEmpty => _history.IsEmpty;

    public string TargetSummary =>
        RecordingCenterDisplayLogic.TargetSummary(Targets.Count, Targets.Count(item => item.IsRecording));

    public string HistorySummary => _history.Summary;

    /// <summary>由 MainWindowViewModel 在弹幕初始化之后调用（幂等）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        _recordings.ProgressUpdated += OnProgressUpdated;
        _recordings.SessionStarted += OnSessionStarted;
        _recordings.SessionCompleted += OnSessionCompleted;
        _recordings.StatusMessage += OnStatusMessage;

        await RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>重新读取全局目录、直播间、录制偏好与历史录像；录制中的行会回填当前指标。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var configured = await _settings
                .GetAsync(AppSettingKeys.RecordingDefaultDirectory, ct)
                .ConfigureAwait(true);
            GlobalDirectory = RecordingCenterDisplayLogic.NormalizeDirectory(configured, RecordingManager.DefaultDirectory);

            var rooms = await _rooms.GetAllAsync(ct).ConfigureAwait(true);
            var targets = await _targets.GetAllAsync(ct).ConfigureAwait(true);
            var targetMap = new Dictionary<Guid, RecordingTarget>();
            foreach (var target in targets)
            {
                targetMap[target.LiveRoomId] = target;
            }

            Targets.Clear();
            foreach (var room in rooms.OrderBy(room => room.DisplayNumber))
            {
                targetMap.TryGetValue(room.Id, out var target);
                Targets.Add(new RecordingTargetItemViewModel(room, target, this));
            }

            // 首屏先按主库已知开播状态回填并排序：录制中 > 直播中 > 其他（组内按显示序号）。
            RefreshLiveStatuses();

            NotifyCollectionsChanged();
            await RefreshHistoryAsync(ct).ConfigureAwait(true);
            StatusText = RecordingCenterDisplayLogic.RefreshStatusText(Targets.Count, History.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.RefreshFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>只刷新历史录像列表（开始/结束录制事件后调用）。历史实现见 <see cref="RecordingHistoryViewModel"/>。</summary>
    public Task RefreshHistoryAsync(CancellationToken ct = default) => _history.RefreshAsync(ct);

    [RelayCommand]
    private async Task Refresh(CancellationToken ct) => await RefreshAsync(ct).ConfigureAwait(true);

    /// <summary>修改全局录像保存目录：写入 AppSettings，下一次开始录制时生效。</summary>
    [RelayCommand]
    private async Task EditGlobalDirectoryAsync()
    {
        if (PickDirectoryAsync is null)
        {
            StatusText = RecordingCenterStatusText.GlobalDirectoryPickerMissing();
            return;
        }

        string? picked;
        try
        {
            picked = await PickDirectoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.DirectoryPickerFailed(ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        try
        {
            var normalized = RecordingCenterDisplayLogic.NormalizeDirectory(picked, RecordingManager.DefaultDirectory);
            Directory.CreateDirectory(normalized);
            // 选择时立刻预检可写性，避免录制开始才发现写不进去（普通用户机器常见问题）。
            RecordingDirectoryGuard.EnsureWritable(normalized);
            await _settings
                .SetAsync(AppSettingKeys.RecordingDefaultDirectory, normalized)
                .ConfigureAwait(true);
            GlobalDirectory = normalized;
            StatusText = RecordingCenterStatusText.GlobalDirectoryUpdated(normalized);
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.GlobalDirectoryUpdateFailed(ex.Message);
        }
    }

    [RelayCommand]
    private void OpenGlobalDirectory()
    {
        try
        {
            Directory.CreateDirectory(GlobalDirectory);
            if (ExplorerLauncher.TryOpenDirectory(GlobalDirectory, out var error))
            {
                StatusText = RecordingCenterStatusText.DirectoryOpened(GlobalDirectory);
            }
            else
            {
                StatusText = ExplorerLauncher.DescribeFailure(GlobalDirectory, error);
            }
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.OpenDirectoryFailed(ex.Message, GlobalDirectory);
        }
    }

    /// <summary>为单个直播间设置独立保存目录（覆盖全局目录）。</summary>
    internal async Task ChooseRoomDirectoryAsync(RecordingTargetItemViewModel item)
    {
        if (PickDirectoryAsync is null)
        {
            StatusText = RecordingCenterStatusText.RoomDirectoryPickerMissing();
            return;
        }

        string? picked;
        try
        {
            picked = await PickDirectoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.DirectoryPickerFailed(ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        try
        {
            var normalized = RecordingCenterDisplayLogic.NormalizeDirectory(picked, RecordingManager.DefaultDirectory);
            // 独立目录同样先做可写性预检，避免自动录制时才发现目录不可写。
            RecordingDirectoryGuard.EnsureWritable(normalized);
            await _recordings
                .SetAutoRecordAsync(item.RoomId, item.AutoRecordEnabled, normalized)
                .ConfigureAwait(true);
            item.ApplyCustomDirectory(normalized);
            StatusText = RecordingCenterStatusText.RoomDirectoryUpdated(item.DisplayTitle, normalized);
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.RoomDirectoryUpdateFailed(ex.Message);
        }
    }

    /// <summary>清除独立目录，改为跟随全局目录。</summary>
    internal async Task ClearRoomDirectoryAsync(RecordingTargetItemViewModel item)
    {
        try
        {
            await _recordings
                .SetAutoRecordAsync(item.RoomId, item.AutoRecordEnabled, null)
                .ConfigureAwait(true);
            item.ApplyCustomDirectory(null);
            StatusText = RecordingCenterStatusText.RoomDirectoryCleared(item.DisplayTitle, GlobalDirectory);
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.RoomDirectoryClearFailed(ex.Message);
        }
    }

    internal Task OpenTargetDirectoryAsync(RecordingTargetItemViewModel item)
    {
        var directory = item.HasCustomDirectory
            ? item.CustomSaveDirectory!
            : GlobalDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            if (ExplorerLauncher.TryOpenDirectory(directory, out var error))
            {
                StatusText = RecordingCenterStatusText.DirectoryOpened(directory);
            }
            else
            {
                StatusText = ExplorerLauncher.DescribeFailure(directory, error);
            }
        }
        catch (Exception ex)
        {
            StatusText = RecordingCenterStatusText.OpenDirectoryFailed(ex.Message, directory);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 边录边剪：把正在录制的直播间送进剪辑台（读取仍在写入的分片）。
    /// 实现见 <see cref="RecordingHistoryViewModel"/>，这里保留录制目标卡片的调用入口。
    /// </summary>
    internal Task RequestOpenLiveClipAsync(Guid liveRoomId) => _history.RequestOpenLiveClipAsync(liveRoomId);
    RecordingManager IRecordingHistoryHost.Recordings => _recordings;

    Func<RecordingHistoryItemViewModel, Task<bool>>? IRecordingHistoryHost.ConfirmDeleteAsync => ConfirmDeleteAsync;

    Func<RecordingSession, Task>? IRecordingHistoryHost.OpenClipStudioAsync => OpenClipStudioAsync;

    bool IRecordingHistoryHost.IsDisposed => _disposed;

    void IRecordingHistoryHost.ReportStatus(string message) => ReportStatus(message);

    Task IRecordingHistoryHost.RefreshCenterAsync(CancellationToken ct) => RefreshAsync(ct);

    internal void ReportStatus(string message)
    {
        if (!_disposed)
        {
            StatusText = message;
        }
    }

    internal void NotifyRecordingStateChanged()
    {
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(IsTargetsEmpty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized)
        {
            _recordings.ProgressUpdated -= OnProgressUpdated;
            _recordings.SessionStarted -= OnSessionStarted;
            _recordings.SessionCompleted -= OnSessionCompleted;
            _recordings.StatusMessage -= OnStatusMessage;
        }

        foreach (var item in Targets)
        {
            item.Dispose();
        }

        _history.Dispose();
    }

    private void OnProgressUpdated(Guid liveRoomId, RecordingProgress progress)
        => PostToUi(() => ApplyProgress(liveRoomId, progress));

    private void OnSessionStarted(RecordingSession session)
        => PostToUi(() => HandleSessionStarted(session));

    private void OnSessionCompleted(RecordingSession session)
        => PostToUi(() => HandleSessionCompleted(session));

    private void OnStatusMessage(string message)
        => PostToUi(() =>
        {
            if (!_disposed && !string.IsNullOrWhiteSpace(message))
            {
                StatusText = message;
            }
        });

    private void ApplyProgress(Guid liveRoomId, RecordingProgress progress)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var item in Targets)
        {
            if (item.RoomId == liveRoomId)
            {
                item.ApplyProgress(progress);
            }
        }

        foreach (var item in History)
        {
            if (item.SessionId == progress.SessionId)
            {
                item.ApplyProgress(progress);
            }
        }

        OnPropertyChanged(nameof(TargetSummary));
    }

    private void HandleSessionStarted(RecordingSession session)
    {
        if (_disposed)
        {
            return;
        }

        if (session.LiveRoomId is { } roomId)
        {
            foreach (var item in Targets)
            {
                if (item.RoomId == roomId)
                {
                    item.ApplySessionStarted(session);
                }
            }
        }

        // 该房间刚进入录制态：立即提到列表最前。
        SortTargets();
        StatusText = RecordingCenterStatusText.RecordingStarted(session.RoomDisplayName, session.VideoFilePath);
        OnPropertyChanged(nameof(TargetSummary));
        _ = RefreshHistoryAsync();
    }

    private void HandleSessionCompleted(RecordingSession session)
    {
        if (_disposed)
        {
            return;
        }

        if (session.LiveRoomId is { } roomId)
        {
            foreach (var item in Targets)
            {
                if (item.RoomId == roomId)
                {
                    item.ApplySessionCompleted(session);
                }
            }
        }

        // 录制结束：该行从「录制中」降级，按直播中/显示序号重新归位。
        SortTargets();
        var status = RecordingUiFormat.StatusText(session.Status);
        StatusText = string.IsNullOrWhiteSpace(session.ErrorMessage)
            ? $"录制结束（{status}）：{session.RoomDisplayName}"
            : $"录制结束（{status}）：{session.RoomDisplayName} · {session.ErrorMessage}";
        OnPropertyChanged(nameof(TargetSummary));
        _ = RefreshHistoryAsync();
    }

    /// <summary>
    /// 用主库已知开播状态回填所有目标行并重新排序（不重建行，滚动位置与行内状态保持）。
    /// MainWindowViewModel 在开播状态轮询后调用；录制开始/结束时也会走一次。
    /// </summary>
    internal void RefreshLiveStatuses()
    {
        if (_disposed)
        {
            return;
        }

        var probe = TryGetLiveStatus;
        foreach (var item in Targets)
        {
            item.ApplyLiveStatus(probe?.Invoke(item.RoomId));
        }

        SortTargets();
    }

    /// <summary>
    /// 就地重排目标列表：录制中 &gt; 直播中 &gt; 其他，组内按显示序号升序。
    /// 用 <see cref="ObservableCollection{T}.Move"/> 移动已有行实例，避免重建导致行内指标与命令状态闪断。
    /// </summary>
    private void SortTargets()
    {
        if (_disposed || Targets.Count <= 1)
        {
            return;
        }

        var ordered = RecordingTargetOrdering.OrderByPriority(
            Targets,
            item => item.IsRecording,
            item => item.IsLive,
            item => item.Room.DisplayNumber);

        for (var index = 0; index < ordered.Count; index++)
        {
            var currentIndex = Targets.IndexOf(ordered[index]);
            if (currentIndex != index)
            {
                Targets.Move(currentIndex, index);
            }
        }
    }

    private void NotifyCollectionsChanged()
    {
        OnPropertyChanged(nameof(IsTargetsEmpty));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(HistorySummary));
    }

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

}
