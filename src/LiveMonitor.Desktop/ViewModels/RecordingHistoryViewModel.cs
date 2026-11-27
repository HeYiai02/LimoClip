using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 录制中心历史录像子视图模型需要的宿主能力。由 <see cref="RecordingCenterViewModel"/> 实现：
/// 历史区自己只处理列表、打开/删除/进入剪辑台流程，跨区域的状态文案与整页刷新仍由录制中心编排。
/// </summary>
internal interface IRecordingHistoryHost
{
    /// <summary>录制引擎（删除记录、读取实时录制会话）。</summary>
    RecordingManager Recordings { get; }

    /// <summary>删除历史录像前的确认弹窗（由窗口注入；null=未接入）。</summary>
    Func<RecordingHistoryItemViewModel, Task<bool>>? ConfirmDeleteAsync { get; }

    /// <summary>把场次送进剪辑台（由主窗口注入；null=未接入）。</summary>
    Func<RecordingSession, Task>? OpenClipStudioAsync { get; }

    /// <summary>录制中心是否已释放。</summary>
    bool IsDisposed { get; }

    /// <summary>把提示写到录制中心状态栏。</summary>
    void ReportStatus(string message);

    /// <summary>边录边剪拿不到实时会话时，退化为刷新整页（与抽取前行为一致）。</summary>
    Task RefreshCenterAsync(CancellationToken ct = default);
}

/// <summary>
/// V0.7.5：录制中心「历史录像」子视图模型。负责最近录像列表的加载（200 条上限、版本号防串写）、
/// 资源管理器定位/打开、删除确认后连 .mkv/.jsonl 删除、进入剪辑台与边录边剪入口。
/// 历史行 <see cref="RecordingHistoryItemViewModel"/> 的命令回调本类；录制中心的刷新/文案/弹窗注入
/// 通过 <see cref="IRecordingHistoryHost"/> 反向调用，保持原有交互与文案逐字不变。
/// </summary>
public sealed partial class RecordingHistoryViewModel : ObservableObject
{
    private const int MaxRecentSessions = 200;

    private readonly IRecordingSessionRepository _sessions;
    private readonly TryLaunch? _openDirectory;
    private readonly TryLaunch? _selectFile;
    private readonly IRecordingHistoryHost _host;
    private int _version;
    private bool _disposed;

    /// <param name="openDirectory">资源管理器打开目录的接缝；null 使用系统实现（测试可注入）。</param>
    /// <param name="selectFile">资源管理器定位文件的接缝；null 使用系统实现（测试可注入）。</param>
    internal RecordingHistoryViewModel(
        IRecordingSessionRepository sessions,
        IRecordingHistoryHost host,
        TryLaunch? openDirectory = null,
        TryLaunch? selectFile = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _openDirectory = openDirectory;
        _selectFile = selectFile;
    }

    /// <summary>最近录像列表（按开始时间倒序）。历史行的删除/打开文件/剪辑命令均指向本视图模型。</summary>
    public ObservableCollection<RecordingHistoryItemViewModel> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public string Summary => $"最近 {Items.Count} 条录像";

    /// <summary>只刷新历史录像列表（开始/结束录制事件后调用）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed || _host.IsDisposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _version);
        try
        {
            var sessions = await _sessions.GetRecentAsync(MaxRecentSessions, ct).ConfigureAwait(true);
            if (_disposed || version != Volatile.Read(ref _version))
            {
                return;
            }

            Items.Clear();
            foreach (var session in sessions)
            {
                Items.Add(new RecordingHistoryItemViewModel(session, this));
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryRefreshFailed(ex.Message));
        }
    }

    /// <summary>用资源管理器定位录像文件；文件不存在时退回到所在目录。</summary>
    /// <summary>合并失败/崩溃恢复后，按分片重试合并并刷新列表与状态栏。</summary>
    internal async Task RetryMergeAsync(RecordingHistoryItemViewModel item)
    {
        if (_host.IsDisposed)
        {
            return;
        }

        SegmentMergeResult result;
        try
        {
            result = await _host.Recordings.RetryMergeAsync(item.Session.Id).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryRetryMergeFailed(ex.Message));
            return;
        }

        _host.ReportStatus(result.Merged
            ? RecordingCenterStatusText.HistoryRetryMergeSucceeded(item.RoomTitle, result.Warning)
            : RecordingCenterStatusText.HistoryRetryMergeFailed(result.ErrorMessage ?? "未知原因"));
        await RefreshAsync().ConfigureAwait(true);
    }

    internal Task OpenFolderAsync(RecordingHistoryItemViewModel item)
    {
        try
        {
            var path = item.VideoFilePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                if ((_selectFile ?? ExplorerLauncher.TrySelectFile)(path, out var selectError))
                {
                    _host.ReportStatus(RecordingCenterStatusText.HistoryFileLocated(item.VideoFileName));
                }
                else
                {
                    _host.ReportStatus(RecordingCenterStatusText.HistoryFileLocateFailed(selectError, path));
                }

                return Task.CompletedTask;
            }

            var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                if ((_openDirectory ?? ExplorerLauncher.TryOpenDirectory)(directory, out var openError))
                {
                    _host.ReportStatus(RecordingCenterStatusText.HistoryFileMissingOpened(directory));
                }
                else
                {
                    _host.ReportStatus(ExplorerLauncher.DescribeFailure(directory, openError));
                }

                return Task.CompletedTask;
            }

            _host.ReportStatus(RecordingCenterStatusText.HistoryFileOrDirectoryMissing());
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryFolderOpenFailed(ex.Message));
        }

        return Task.CompletedTask;
    }

    /// <summary>删除历史录像：在录会话直接拒绝；弹窗确认后连 .mkv/.jsonl 一起删除。</summary>
    internal async Task RequestDeleteAsync(RecordingHistoryItemViewModel item)
    {
        if (_host.IsDisposed)
        {
            return;
        }

        if (item.IsActive)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryDeleteRejectedActive());
            return;
        }

        var confirmDeleteAsync = _host.ConfirmDeleteAsync;
        if (confirmDeleteAsync is null)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryDeleteConfirmMissing());
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await confirmDeleteAsync(item).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryDeleteConfirmFailed(ex.Message));
            return;
        }

        if (!confirmed || _host.IsDisposed)
        {
            return;
        }

        try
        {
            await _host.Recordings.DeleteSessionAsync(item.SessionId).ConfigureAwait(true);
            _host.ReportStatus(RecordingCenterStatusText.HistoryDeleted(item.RoomTitle, item.StartedAtText));
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.HistoryDeleteFailed(ex.Message));
        }
    }

    /// <summary>V0.4：把已完成的录像送进剪辑台；在录/文件缺失时只提示不跳转。</summary>
    internal async Task RequestOpenClipAsync(RecordingHistoryItemViewModel item)
    {
        if (_host.IsDisposed)
        {
            return;
        }

        if (item.IsActive)
        {
            _host.ReportStatus(RecordingCenterStatusText.ClipRejectedActive());
            return;
        }

        if (string.IsNullOrWhiteSpace(item.VideoFilePath) || !File.Exists(item.VideoFilePath))
        {
            _host.ReportStatus(RecordingCenterStatusText.ClipFileMissing(item.VideoFilePath));
            return;
        }

        var openClipStudioAsync = _host.OpenClipStudioAsync;
        if (openClipStudioAsync is null)
        {
            _host.ReportStatus(RecordingCenterStatusText.ClipStudioMissing());
            return;
        }

        try
        {
            await openClipStudioAsync(item.Session).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.OpenClipStudioFailed(ex.Message));
        }
    }

    /// <summary>
    /// 边录边剪：把正在录制的直播间送进剪辑台（读取仍在写入的分片）。
    /// 录制刚结束、拿不到实时信息时退化为刷新列表。
    /// </summary>
    internal async Task RequestOpenLiveClipAsync(Guid liveRoomId)
    {
        if (_host.IsDisposed)
        {
            return;
        }

        var live = _host.Recordings.GetLiveRecordings()
            .FirstOrDefault(recording => recording.LiveRoomId == liveRoomId);
        if (live is null)
        {
            _host.ReportStatus(RecordingCenterStatusText.LiveClipJustEnded());
            await _host.RefreshCenterAsync().ConfigureAwait(true);
            return;
        }

        var openClipStudioAsync = _host.OpenClipStudioAsync;
        if (openClipStudioAsync is null)
        {
            _host.ReportStatus(RecordingCenterStatusText.ClipStudioMissing());
            return;
        }

        try
        {
            var session = await _sessions.GetByIdAsync(live.SessionId).ConfigureAwait(true);
            if (session is null)
            {
                _host.ReportStatus(RecordingCenterStatusText.LiveClipJustEnded());
                await _host.RefreshCenterAsync().ConfigureAwait(true);
                return;
            }

            await openClipStudioAsync(session).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.ReportStatus(RecordingCenterStatusText.OpenClipStudioFailed(ex.Message));
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
