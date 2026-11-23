using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// V0.3 录制中心：全局保存目录、按直播间的录制目标列表（自动录制/立即开录/实时指标）
/// 以及历史录像列表（打开文件夹/删除记录）。录制引擎在后台常驻，关闭本页不中断录制。
/// </summary>
public sealed partial class RecordingCenterViewModel : ObservableObject, IDisposable
{
    private readonly IRoomRepository _rooms;
    private readonly IRecordingTargetRepository _targets;
    private readonly IRecordingSessionRepository _sessions;
    private readonly IAppSettingsRepository _settings;
    private readonly RecordingManager _recordings;
    private bool _initialized;
    private bool _disposed;
    private int _historyVersion;
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
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
    }

    public ObservableCollection<RecordingTargetItemViewModel> Targets { get; } = new();

    public ObservableCollection<RecordingHistoryItemViewModel> History { get; } = new();

    /// <summary>由 MainWindow 注入：删除历史录像前的确认弹窗，返回 true 才真正删除。</summary>
    public Func<RecordingHistoryItemViewModel, Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>由 MainWindow 注入：系统目录选择器，返回绝对路径；null 表示用户取消。</summary>
    public Func<Task<string?>>? PickDirectoryAsync { get; set; }

    /// <summary>V0.4：由 MainWindowViewModel 注入，录制中心请求把指定场次送进剪辑台。</summary>
    public Func<RecordingSession, Task>? OpenClipStudioAsync { get; set; }

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

    public bool IsHistoryEmpty => History.Count == 0;

    public string TargetSummary =>
        $"共 {Targets.Count} 个直播间 · 正在录制 {Targets.Count(item => item.IsRecording)} 路";

    public string HistorySummary => $"最近 {History.Count} 条录像";

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
            GlobalDirectory = NormalizeDirectory(configured);

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

            NotifyCollectionsChanged();
            await RefreshHistoryAsync(ct).ConfigureAwait(true);
            StatusText = $"已刷新：{Targets.Count} 个直播间 · {History.Count} 条历史录像";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"刷新录制中心失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>只刷新历史录像列表（开始/结束录制事件后调用）。</summary>
    public async Task RefreshHistoryAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _historyVersion);
        try
        {
            var sessions = await _sessions.GetRecentAsync(200, ct).ConfigureAwait(true);
            if (_disposed || version != Volatile.Read(ref _historyVersion))
            {
                return;
            }

            History.Clear();
            foreach (var session in sessions)
            {
                History.Add(new RecordingHistoryItemViewModel(session, this));
            }

            OnPropertyChanged(nameof(IsHistoryEmpty));
            OnPropertyChanged(nameof(HistorySummary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"读取录制历史失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Refresh(CancellationToken ct) => await RefreshAsync(ct).ConfigureAwait(true);

    /// <summary>修改全局录像保存目录：写入 AppSettings，下一次开始录制时生效。</summary>
    [RelayCommand]
    private async Task EditGlobalDirectoryAsync()
    {
        if (PickDirectoryAsync is null)
        {
            StatusText = "当前界面未接入目录选择器，无法修改全局目录。";
            return;
        }

        string? picked;
        try
        {
            picked = await PickDirectoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录选择器失败：{ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        try
        {
            var normalized = NormalizeDirectory(picked);
            Directory.CreateDirectory(normalized);
            await _settings
                .SetAsync(AppSettingKeys.RecordingDefaultDirectory, normalized)
                .ConfigureAwait(true);
            GlobalDirectory = normalized;
            StatusText = $"全局录像目录已更新：{normalized}（新开始的录制生效）";
        }
        catch (Exception ex)
        {
            StatusText = $"修改全局录像目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenGlobalDirectory()
    {
        try
        {
            Directory.CreateDirectory(GlobalDirectory);
            OpenInExplorer(GlobalDirectory);
            StatusText = $"已打开目录：{GlobalDirectory}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开全局目录失败：{ex.Message}";
        }
    }

    /// <summary>为单个直播间设置独立保存目录（覆盖全局目录）。</summary>
    internal async Task ChooseRoomDirectoryAsync(RecordingTargetItemViewModel item)
    {
        if (PickDirectoryAsync is null)
        {
            StatusText = "当前界面未接入目录选择器，无法设置独立目录。";
            return;
        }

        string? picked;
        try
        {
            picked = await PickDirectoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录选择器失败：{ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        try
        {
            var normalized = NormalizeDirectory(picked);
            await _recordings
                .SetAutoRecordAsync(item.RoomId, item.AutoRecordEnabled, normalized)
                .ConfigureAwait(true);
            item.ApplyCustomDirectory(normalized);
            StatusText = $"「{item.DisplayTitle}」的独立保存目录已设置为：{normalized}";
        }
        catch (Exception ex)
        {
            StatusText = $"设置独立目录失败：{ex.Message}";
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
            StatusText = $"「{item.DisplayTitle}」已改为跟随全局目录：{GlobalDirectory}";
        }
        catch (Exception ex)
        {
            StatusText = $"清除独立目录失败：{ex.Message}";
        }
    }

    internal Task OpenTargetDirectoryAsync(RecordingTargetItemViewModel item)
    {
        try
        {
            var directory = item.HasCustomDirectory
                ? item.CustomSaveDirectory!
                : GlobalDirectory;
            Directory.CreateDirectory(directory);
            OpenInExplorer(directory);
            StatusText = $"已打开目录：{directory}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>用资源管理器定位录像文件；文件不存在时退回到所在目录。</summary>
    internal Task OpenHistoryFolderAsync(RecordingHistoryItemViewModel item)
    {
        try
        {
            var path = item.VideoFilePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                SelectInExplorer(path);
                StatusText = $"已在资源管理器中定位：{item.VideoFileName}";
                return Task.CompletedTask;
            }

            var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                OpenInExplorer(directory);
                StatusText = $"录像文件不存在，已打开所在目录：{directory}";
                return Task.CompletedTask;
            }

            StatusText = "找不到录像文件或所在目录（可能已被手动删除）。";
        }
        catch (Exception ex)
        {
            StatusText = $"打开文件夹失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    /// <summary>删除历史录像：在录会话直接拒绝；弹窗确认后连 .mkv/.jsonl 一起删除。</summary>
    internal async Task RequestDeleteAsync(RecordingHistoryItemViewModel item)
    {
        if (item.IsActive)
        {
            StatusText = "该录制仍在进行中，请先停止录制再删除记录。";
            return;
        }

        if (ConfirmDeleteAsync is null)
        {
            StatusText = "当前界面未接入删除确认弹窗，已取消删除。";
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await ConfirmDeleteAsync(item).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"删除确认失败：{ex.Message}";
            return;
        }

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _recordings.DeleteSessionAsync(item.SessionId).ConfigureAwait(true);
            StatusText = $"已删除录制记录：{item.RoomTitle} · {item.StartedAtText}";
            await RefreshHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"删除录制记录失败：{ex.Message}";
        }
    }

    /// <summary>V0.4：把已完成的录像送进剪辑台；在录/文件缺失时只提示不跳转。</summary>
    internal async Task RequestOpenClipAsync(RecordingHistoryItemViewModel item)
    {
        if (item.IsActive)
        {
            StatusText = "该录制仍在进行中，请先停止录制再进入剪辑台。";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.VideoFilePath) || !File.Exists(item.VideoFilePath))
        {
            StatusText = $"录像文件不存在，无法剪辑：{item.VideoFilePath}";
            return;
        }

        if (OpenClipStudioAsync is null)
        {
            StatusText = "当前界面未接入剪辑台。";
            return;
        }

        try
        {
            await OpenClipStudioAsync(item.Session).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"打开剪辑台失败：{ex.Message}";
        }
    }

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

        StatusText = $"开始录制：{session.RoomDisplayName} → {session.VideoFilePath}";
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

        var status = RecordingUiFormat.StatusText(session.Status);
        StatusText = string.IsNullOrWhiteSpace(session.ErrorMessage)
            ? $"录制结束（{status}）：{session.RoomDisplayName}"
            : $"录制结束（{status}）：{session.RoomDisplayName} · {session.ErrorMessage}";
        OnPropertyChanged(nameof(TargetSummary));
        _ = RefreshHistoryAsync();
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

    private static string NormalizeDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RecordingManager.DefaultDirectory;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim()));
        }
        catch
        {
            return raw.Trim();
        }
    }

    private static void OpenInExplorer(string path)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
        {
            UseShellExecute = true
        });

    private static void SelectInExplorer(string filePath)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
        {
            UseShellExecute = true
        });
}
