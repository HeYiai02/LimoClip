using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Controls;
using LiveMonitor.Media.Editing;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// V0.4 剪辑台 ViewModel：录像场次/本地文件加载、libmpv 预览控制、弹幕回放同步、
/// 双波形时间轴选区与双版切片导出。所有播放器/控件交互都通过事件交给 View 桥接，
/// 本类不直接引用 Avalonia 控件，保证可独立演进与测试。
/// </summary>
public sealed partial class ClipStudioViewModel : ObservableObject, IDisposable, IClipAsrHost, IClipExportHost
{
    private const int MaxSessionCount = 100;
    private const int MaxHighlightCount = 500;

    private readonly IRecordingSessionRepository _sessions;
    private readonly IHighlightEventRepository _highlights;
    private readonly VideoProbeService _videoProbe;
    private readonly IWaveformService _waveforms;
    private readonly IHardwareEncoderDetector _encoders;
    private readonly IClipExporter _exporter;
    private readonly ILiveRecordingMonitor _liveRecordings;
    private readonly ILiveWaveformExtractor _liveWaveforms;

    private bool _initialized;
    private bool _disposed;
    private int _loadVersion;
    private bool _suppressSessionOpen;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _waveformCts;

    private ClipSessionOption? _selectedSession;
    private RecordingSession? _currentSession;
    private string? _currentVideoPath;
    private string? _currentDanmakuPath;
    private string _sourceVideoCodec = "h264";
    private IReadOnlyList<RecordedDanmakuItem>? _danmakuItems;
    private DanmakuPlaybackCursor? _cursor;
    private WaveformData? _waveform;
    private IReadOnlyList<ClipHighlightMarker> _highlightMarkers = Array.Empty<ClipHighlightMarker>();
    private TimeSpan _duration;
    private TimeSpan _position;
    private TimeSpan _inPoint;
    private TimeSpan _outPoint;
    private double _speed = 1.0;
    private ClipSpeedOption _selectedSpeedOption;
    private string _videoTitle = "未加载视频";
    private string _videoSubtitle = "从左侧选择录像场次，或打开本地 mkv / mp4 文件";
    private string _encoderText = ClipStudioStatusText.EncoderAutoDetecting;
    private string _statusText = "从左侧选择录像，或打开本地视频开始剪辑。";
    private bool _isWaveformLoading;
    private bool _isPlaying;
    private bool _isBusy;
    private bool _danmakuPlaybackEnabled = true;

    // ---- 边录边剪状态 ----
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private readonly LiveClipSession _liveSession = new();
    private readonly LiveSessionCoordinator _liveSync;
    private bool _isAutoSyncEnabled = true;

    public ClipStudioViewModel(
        IRecordingSessionRepository sessions,
        IHighlightEventRepository highlights,
        IAppSettingsRepository settings,
        VideoProbeService videoProbe,
        IWaveformService waveforms,
        IHardwareEncoderDetector encoders,
        IClipExporter exporter,
        ILiveRecordingMonitor liveRecordings,
        ILiveWaveformExtractor liveWaveforms,
        IAsrProvider asr)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _highlights = highlights ?? throw new ArgumentNullException(nameof(highlights));
        settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _videoProbe = videoProbe ?? throw new ArgumentNullException(nameof(videoProbe));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _liveRecordings = liveRecordings ?? throw new ArgumentNullException(nameof(liveRecordings));
        _liveWaveforms = liveWaveforms ?? throw new ArgumentNullException(nameof(liveWaveforms));
        _liveSync = new LiveSessionCoordinator(
            _liveSession,
            _liveRecordings,
            _liveWaveforms,
            new DanmakuJsonlReaderAdapter(),
            new DispatcherLiveSessionTimer(),
            new LiveSessionSyncHost(this),
            new LiveSessionOpenHost(this));
        Export = new ClipExportViewModel(this, settings);
        Asr = new ClipAsrViewModel(asr ?? throw new ArgumentNullException(nameof(asr)), this);
        _liveRecordings.SessionCompleted += OnLiveRecordingSessionCompleted;

        SpeedOptions = new ReadOnlyCollection<ClipSpeedOption>(new[]
        {
            new ClipSpeedOption(0.5, "0.5x"),
            new ClipSpeedOption(0.75, "0.75x"),
            new ClipSpeedOption(1.0, "1.0x"),
            new ClipSpeedOption(1.25, "1.25x"),
            new ClipSpeedOption(1.5, "1.5x"),
            new ClipSpeedOption(2.0, "2.0x")
        });
        _selectedSpeedOption = SpeedOptions[2];
    }

    /// <summary>由 View 注入：选择本地视频文件（mkv/mp4），返回绝对路径；null 表示取消。</summary>
    public Func<Task<string?>>? PickVideoFileAsync { get; set; }

    /// <summary>由 View 注入：选择切片导出目录，返回绝对路径；null 表示取消。转发到导出子视图模型。</summary>
    public Func<Task<string?>>? PickExportDirectoryAsync
    {
        get => Export.PickExportDirectoryAsync;
        set => Export.PickExportDirectoryAsync = value;
    }

    /// <summary>时间轴/选区/波形数据变化，由 View 重新拉取后写入 ClipTimelineControl。</summary>
    public event EventHandler? TimelineDataChanged;

    /// <summary>请求 View 通过本地播放器加载指定文件。</summary>
    public event Action<string>? LoadVideoRequested;

    /// <summary>请求播放/暂停（暂停状态视为一次切换）。</summary>
    public event Action? PlayPauseRequested;

    /// <summary>直接暂停（切换视图/离开剪辑台时避免后台继续出声）。</summary>
    public event Action? PauseRequested;

    /// <summary>请求播放器精确 seek。</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>请求播放器切换倍速（0.5x ~ 2.0x）。</summary>
    public event Action<double>? SpeedChangeRequested;

    /// <summary>弹幕回放开关变化。</summary>
    public event Action<bool>? DanmakuEnabledChanged;

    /// <summary>批量推送回放弹幕；clearFirst=true 表示先清空（seek 后重建画面）。</summary>
    public event Action<IReadOnlyList<DanmakuMessage>, bool>? DanmakuSyncRequested;

    /// <summary>边录边剪：请求 View 用“可安全 seek”的方式重载当前录制分片（保持播放位置）。</summary>
    public event Action? LiveReloadRequested;

    /// <summary>边录边剪：请求 View 以 live 模式加载录制中的分片文件。</summary>
    public event Action<string>? LoadLiveVideoRequested;

    /// <summary>边录边剪：安全可剪截止时间变化，View 据此放宽播放器的 seek 上界（无需重载）。</summary>
    public event Action? LiveSafeDurationChanged;

    public ObservableCollection<ClipSessionOption> Sessions { get; } = new();

    public ObservableCollection<ClipHighlightItemViewModel> Highlights { get; } = new();

    /// <summary>剪辑台「语音识别」子视图模型：面板状态、命令与结果行统一由它维护。</summary>
    public ClipAsrViewModel Asr { get; }

    /// <summary>剪辑台「切片导出」子视图模型：导出面板状态、命令与导出队列统一由它维护。</summary>
    public ClipExportViewModel Export { get; }

    public IReadOnlyList<ClipSpeedOption> SpeedOptions { get; }

    // ---- 场次与文件 ----

    public ClipSessionOption? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value))
            {
                return;
            }

            if (_suppressSessionOpen || value is null)
            {
                return;
            }

            _ = OpenSessionAsync(value.Session);
        }
    }

    public string? CurrentVideoPath => _currentVideoPath;

    public string? CurrentDanmakuPath => _currentDanmakuPath;

    public bool HasVideo => _currentVideoPath is not null && _duration > TimeSpan.Zero;

    public bool HasDanmaku => _danmakuItems is { Count: > 0 };

    public string DanmakuInfoText => ClipStudioPresentationLogic.BuildDanmakuInfoText(_danmakuItems, IsLiveSession);

    /// <summary>已加载的弹幕条数（边录边剪同步后会持续增长）。</summary>
    public int DanmakuItemCount => _danmakuItems?.Count ?? 0;

    public string VideoTitle
    {
        get => _videoTitle;
        private set => SetProperty(ref _videoTitle, value);
    }

    public string VideoSubtitle
    {
        get => _videoSubtitle;
        private set => SetProperty(ref _videoSubtitle, value);
    }

    // ---- 时间与选区 ----

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (!SetProperty(ref _duration, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(HasVideo));
            Export.NotifyAvailabilityChanged();
            Asr.NotifyAvailabilityChanged();
        }
    }

    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public TimeSpan InPoint => _inPoint;

    public TimeSpan OutPoint => _outPoint;

    public string DurationText => TimeCodeFormat.Clock(_duration);

    public string PositionText => ClipStudioPresentationLogic.BuildPositionText(_position, _duration);

    public string InText => TimeCodeFormat.Clock(_inPoint);

    public string OutText => TimeCodeFormat.Clock(_outPoint);

    public string SelectionText => ClipStudioPresentationLogic.BuildSelectionText(_duration, _inPoint, _outPoint);

    public WaveformData? Waveform
    {
        get => _waveform;
        private set => SetProperty(ref _waveform, value);
    }

    public IReadOnlyList<ClipHighlightMarker> HighlightMarkers
    {
        get => _highlightMarkers;
        private set => SetProperty(ref _highlightMarkers, value);
    }

    public bool IsWaveformLoading
    {
        get => _isWaveformLoading;
        private set => SetProperty(ref _isWaveformLoading, value);
    }

    // ---- 播放控制 ----

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
            }
        }
    }

    public string PlayPauseGlyph => ClipStudioPresentationLogic.PlayPauseGlyph(_isPlaying);

    public double Speed
    {
        get => _speed;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 2.0);
            if (!SetProperty(ref _speed, clamped))
            {
                return;
            }

            var option = SpeedOptions.FirstOrDefault(item => Math.Abs(item.Value - clamped) < 0.001);
            if (option is not null && !ReferenceEquals(option, _selectedSpeedOption))
            {
                _selectedSpeedOption = option;
                OnPropertyChanged(nameof(SelectedSpeedOption));
            }

            SpeedChangeRequested?.Invoke(clamped);
        }
    }

    public ClipSpeedOption SelectedSpeedOption
    {
        get => _selectedSpeedOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedSpeedOption, value))
            {
                return;
            }

            Speed = value.Value;
        }
    }

    public bool DanmakuPlaybackEnabled
    {
        get => _danmakuPlaybackEnabled;
        set
        {
            if (!SetProperty(ref _danmakuPlaybackEnabled, value))
            {
                return;
            }

            DanmakuEnabledChanged?.Invoke(value);
            if (value)
            {
                ReplayDanmakuAroundPosition();
            }
        }
    }

    // ---- 导出面板 ----

    public string EncoderText
    {
        get => _encoderText;
        private set
        {
            if (SetProperty(ref _encoderText, value))
            {
                Export.NotifyEncoderTextChanged();
            }
        }
    }

    // ---- 导出面板（其余状态、命令与导出队列已抽离到 ClipExportViewModel） ----

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SyncLiveProgressCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // ---- 边录边剪（Live Clipping） ----

    /// <summary>当前是否正在剪辑“录制中”的分片（此时 <see cref="Duration"/> 会随录制增长）。</summary>
    public bool IsLiveSession => _liveSession.IsActive;

    /// <summary>录制中分片已录制的实时时长（含仍在前端写入、尚不可安全剪裁的尾部）。</summary>
    public TimeSpan LiveRecordedDuration => _liveSession.RecordedDuration;

    /// <summary>安全可剪截止时间（已录时长 - 5 秒安全缓冲）；Zero 表示暂不可剪。</summary>
    public TimeSpan SafeClipDuration => _liveSession.SafeClipDuration;

    /// <summary>录制中状态条文案（含已录时长与安全可剪截止）。</summary>
    public string LiveStatusText => _liveSession.StatusText;

    /// <summary>是否每 10 秒自动同步高能/弹幕（不重载播放器、不打断播放）。</summary>
    public bool IsAutoSyncEnabled
    {
        get => _isAutoSyncEnabled;
        set => SetProperty(ref _isAutoSyncEnabled, value);
    }

    /// <summary>是否正在执行同步（防止手动/自动同步重入）。</summary>
    public bool IsSyncingLive => _liveSync.IsSyncing;

    private bool CanSyncLiveProgress => IsLiveSession && !IsBusy && !IsSyncingLive;

    // ---- 初始化 / 场次 ----

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        await Export.InitializeAsync(ct).ConfigureAwait(true);
        await RefreshSessionsAsync(ct).ConfigureAwait(true);
        _ = DetectEncoderAsync();
    }

    public async Task RefreshSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            var sessions = await _sessions.GetRecentAsync(MaxSessionCount, ct).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            var previousId = _selectedSession?.Session.Id;
            _suppressSessionOpen = true;
            try
            {
                Sessions.Clear();
                // 录制中的场次置顶，并携带实时分片信息（边录边剪入口）。
                foreach (var session in sessions)
                {
                    if (_liveRecordings.TryGetLiveRecording(session.Id, out var live))
                    {
                        Sessions.Insert(0, new ClipSessionOption(session, live));
                    }
                    else
                    {
                        Sessions.Add(new ClipSessionOption(session));
                    }
                }
            }
            finally
            {
                _suppressSessionOpen = false;
            }

            if (previousId is { } id)
            {
                SelectedSession = Sessions.FirstOrDefault(item => item.Session.Id == id);
            }
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.SessionListLoadFailed(ex.Message);
        }
    }

    public async Task OpenSessionAsync(RecordingSession session)
    {
        if (session is null || _disposed)
        {
            return;
        }

        // 先让左侧下拉框选中目标场次（live/离线共用），避免 ComboBox 显示与实际剪辑内容不一致。
        var option = Sessions.FirstOrDefault(item => item.Session.Id == session.Id);
        if (option is not null && !ReferenceEquals(option, _selectedSession))
        {
            _suppressSessionOpen = true;
            try
            {
                SelectedSession = option;
            }
            finally
            {
                _suppressSessionOpen = false;
            }
        }

        // 录制中的场次：最终视频文件尚未生成，改为读取正在写入的分片（边录边剪）。
        if (_liveRecordings.TryGetLiveRecording(session.Id, out var liveInfo))
        {
            await OpenLiveSessionAsync(session, liveInfo).ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(session.VideoFilePath) || !File.Exists(session.VideoFilePath))
        {
            StatusText = ClipStudioStatusText.VideoFileMissing(session.VideoFilePath);
            return;
        }

        var danmakuPath = !string.IsNullOrWhiteSpace(session.DanmakuFilePath)
                          && File.Exists(session.DanmakuFilePath)
            ? session.DanmakuFilePath
            : null;

        await OpenVideoCoreAsync(session.VideoFilePath, danmakuPath, session).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenLocalFileAsync()
    {
        if (PickVideoFileAsync is null)
        {
            StatusText = ClipStudioStatusText.LocalFilePickerMissing();
            return;
        }

        string? path;
        try
        {
            path = await PickVideoFileAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.PickFileFailed(ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenVideoCoreAsync(path, ClipStudioPresentationLogic.FindCompanionDanmaku(path), session: null).ConfigureAwait(true);
    }

    private async Task OpenVideoCoreAsync(string videoPath, string? danmakuPath, RecordingSession? session)
    {
        if (!File.Exists(videoPath))
        {
            StatusText = ClipStudioStatusText.VideoPathMissing(videoPath);
            return;
        }

        // 切到离线视频：退出边录边剪状态（定时器/安全截止/分片信息）。
        ExitLiveMode();

        _loadVersion++;
        var version = _loadVersion;
        ReplaceCancellationSource(ref _loadCts);
        ReplaceCancellationSource(ref _waveformCts);
        var ct = _loadCts!.Token;
        var waveformCt = _waveformCts!.Token;

        IsBusy = true;
        StatusText = ClipStudioStatusText.ReadingVideoInfo();
        try
        {
            var metadata = await _videoProbe.ProbeAsync(videoPath, ct).ConfigureAwait(true);
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            _currentSession = session;
            _currentVideoPath = videoPath;
            _currentDanmakuPath = danmakuPath;
            Asr.Reset();
            ResetHighlightAsrState();
            _sourceVideoCodec = string.IsNullOrWhiteSpace(metadata.VideoCodec) ? "h264" : metadata.VideoCodec;
            VideoTitle = Path.GetFileName(videoPath);
            VideoSubtitle = ClipStudioPresentationLogic.BuildVideoSubtitle(session, metadata);

            Duration = metadata.Duration;
            UpdateSelection(TimeSpan.Zero, metadata.Duration);
            Position = TimeSpan.Zero;
            _danmakuItems = null;
            _cursor = null;
            Highlights.Clear();
            HighlightMarkers = Array.Empty<ClipHighlightMarker>();
            Waveform = null;
            OnPropertyChanged(nameof(HasDanmaku));
            OnPropertyChanged(nameof(DanmakuInfoText));

            if (danmakuPath is not null)
            {
                try
                {
                    _danmakuItems = await DanmakuJsonlReader.ReadAllAsync(danmakuPath, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StatusText = ClipStudioStatusText.DanmakuFileLoadFailed(ex.Message);
                }
            }

            if (version != _loadVersion || _disposed)
            {
                return;
            }

            _cursor = new DanmakuPlaybackCursor(_danmakuItems ?? Array.Empty<RecordedDanmakuItem>());
            _cursor.Reset(0);
            OnPropertyChanged(nameof(HasDanmaku));
            OnPropertyChanged(nameof(DanmakuInfoText));

            if (session is not null)
            {
                await LoadHighlightsAsync(session, metadata.Duration, ct).ConfigureAwait(true);
            }

            Export.SetGeneratedOutputFileName(videoPath, _inPoint, _outPoint);
            IsWaveformLoading = true;
            RaiseTimelineChanged();
            DanmakuEnabledChanged?.Invoke(_danmakuPlaybackEnabled);
            DanmakuSyncRequested?.Invoke(Array.Empty<DanmakuMessage>(), true);
            LoadVideoRequested?.Invoke(videoPath);
            StatusText = ClipStudioStatusText.VideoLoaded(Path.GetFileName(videoPath), ClipExportTaskViewModel.FormatTime(metadata.Duration));
            Export.NotifyAvailabilityChanged();

            _ = LoadWaveformAsync(videoPath, danmakuPath, metadata.Duration, waveformCt);
        }
        catch (OperationCanceledException)
        {
            // 用户连续切换录像时取消上一次加载，静默忽略。
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.VideoLoadFailed(ex.Message);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    // ---- 边录边剪（Live Clipping） ----

    /// <summary>进入 live 模式并同步相关属性通知（命令可用性 / 导出面板 / ASR 可用性）。</summary>
    private void StartLiveClipSession(LiveRecordingInfo info, string partPath)
    {
        _liveSession.Start(info, partPath);
        SyncLiveProgressCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLiveSession));
        OnPropertyChanged(nameof(LiveRecordedDuration));
        OnPropertyChanged(nameof(SafeClipDuration));
        OnPropertyChanged(nameof(LiveStatusText));
        OnPropertyChanged(nameof(DanmakuInfoText));
        LiveSafeDurationChanged?.Invoke();
        Export.NotifyAvailabilityChanged();
    }

    /// <summary>应用最新录制进度并同步属性通知（安全截止变化才触发 live 播放器更新）。</summary>
    private void UpdateLiveClipProgress(LiveRecordingInfo latest)
    {
        var previousSafe = _liveSession.SafeClipDuration;
        _liveSession.ApplyLatest(latest, latest.PartFilePath);
        OnPropertyChanged(nameof(LiveRecordedDuration));
        if (previousSafe != _liveSession.SafeClipDuration)
        {
            OnPropertyChanged(nameof(SafeClipDuration));
            LiveSafeDurationChanged?.Invoke();
        }

        OnPropertyChanged(nameof(LiveStatusText));
    }

    /// <summary>
    /// 打开「录制中」的场次：编排（分段拒绝、分片校验、探测兜底、陈旧加载中止、失败文案）
    /// 由 <see cref="LiveSessionCoordinator"/> 负责，本类只提供 UI 副作用（见 <see cref="LiveSessionOpenHost"/>）。
    /// </summary>
    private Task OpenLiveSessionAsync(RecordingSession session, LiveRecordingInfo liveInfo) =>
        _liveSync.OpenAsync(session, liveInfo);

    /// <summary>退出 live 模式（切到离线视频 / 录制结束时重置定时器与安全截止）。</summary>
    private void ExitLiveMode()
    {
        _liveSync.StopAutoSync();
        if (!_liveSession.IsActive)
        {
            return;
        }

        _liveSession.Reset();
        SyncLiveProgressCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLiveSession));
        OnPropertyChanged(nameof(LiveRecordedDuration));
        OnPropertyChanged(nameof(SafeClipDuration));
        OnPropertyChanged(nameof(LiveStatusText));
        OnPropertyChanged(nameof(DanmakuInfoText));
        Export.NotifyAvailabilityChanged();
    }

    /// <summary>按实时已录时长重建固定 0.5s 分辨率的波形数据（音频峰值 + 弹幕密度）。</summary>
    private void RebuildLiveWaveformData()
    {
        if (!IsLiveSession || LiveRecordedDuration <= TimeSpan.Zero)
        {
            return;
        }

        Waveform = _liveSession.BuildWaveform(_danmakuItems);
    }

    /// <summary>手动同步（按钮 / F5）：进度、弹幕、高能、波形全量增量刷新，并重载播放器尾部数据。</summary>
    [RelayCommand(CanExecute = nameof(CanSyncLiveProgress))]
    private Task SyncLiveProgressAsync() => _liveSync.SyncManualAsync();

    /// <summary>录制结束：停止实时同步，并把当前分片无缝切换为最终文件 + 离线剪辑模式。</summary>
    public async Task OnLiveSessionCompletedAsync(RecordingSession session)
    {
        if (session is null || _disposed || !IsLiveSession || _currentSession?.Id != session.Id)
        {
            return;
        }

        _liveSync.StopAutoSync();
        var lastPosition = _position;
        var lastIn = _inPoint;
        var lastOut = _outPoint;
        ExitLiveMode();

        try
        {
            var latest = await _sessions.GetByIdAsync(session.Id).ConfigureAwait(true);
            if (latest is null || string.IsNullOrWhiteSpace(latest.VideoFilePath) || !File.Exists(latest.VideoFilePath))
            {
                StatusText = ClipStudioStatusText.LiveCompletedWithoutFile(latest?.ErrorMessage);
                return;
            }

            var danmakuPath = !string.IsNullOrWhiteSpace(latest.DanmakuFilePath)
                              && File.Exists(latest.DanmakuFilePath)
                ? latest.DanmakuFilePath
                : null;
            await OpenVideoCoreAsync(latest.VideoFilePath, danmakuPath, latest).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            // 录制已结束：刷新列表，让该场次从「🔴 录制中」切换为普通历史项。
            await RefreshSessionsAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (lastOut > TimeSpan.Zero && lastOut > lastIn + ClipSelectionLogic.MinSelection)
            {
                UpdateSelection(lastIn, lastOut);
            }

            SeekTo(lastPosition);
            RaiseTimelineChanged();
            StatusText = ClipStudioStatusText.LiveCompleted;
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.LiveSwitchToOfflineFailed(ex.Message);
        }
    }

    private void OnLiveRecordingSessionCompleted(RecordingSession session)
    {
        if (_disposed || session is null)
        {
            return;
        }

        // 事件可能来自录制线程：有 UI 同步上下文时切回 UI 线程再操作集合。
        if (_syncContext is not null && SynchronizationContext.Current != _syncContext)
        {
            _syncContext.Post(_ => _ = OnLiveSessionCompletedAsync(session), null);
            return;
        }

        _ = OnLiveSessionCompletedAsync(session);
    }

    private async Task LoadHighlightsAsync(RecordingSession session, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var events = await _highlights
                .GetByRecordingSessionAsync(session.Id, MaxHighlightCount, ct)
                .ConfigureAwait(true);
            var startOffset = new DateTimeOffset(session.StartedAt.ToUniversalTime().Ticks, TimeSpan.Zero);
            var projections = ClipStudioPresentationLogic.ProjectHighlights(events, startOffset, duration);
            var markers = new List<ClipHighlightMarker>(projections.Count);
            foreach (var projection in projections)
            {
                Highlights.Add(new ClipHighlightItemViewModel(projection.Event, projection.Position));
                markers.Add(new ClipHighlightMarker(projection.Position, projection.Event.TriggerReason));
            }

            HighlightMarkers = markers;
            RefreshHighlightTranscribeAvailability();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.HighlightLoadFailed(ex.Message);
        }
    }

    private async Task LoadWaveformAsync(
        string videoPath,
        string? danmakuPath,
        TimeSpan duration,
        CancellationToken ct)
    {
        try
        {
            var waveform = await _waveforms
                .GetWaveformAsync(videoPath, duration, danmakuPath, 2000, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            Waveform = waveform;
            RaiseTimelineChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = ClipStudioStatusText.WaveformLoadFailed(ex.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsWaveformLoading = false;
            }
        }
    }

    private async Task DetectEncoderAsync()
    {
        try
        {
            var (encoderName, _) = await _encoders.DetectBestAsync().ConfigureAwait(true);
            EncoderText = ClipStudioStatusText.EncoderLabel(encoderName);
        }
        catch (Exception ex)
        {
            EncoderText = ClipStudioStatusText.EncoderLabel(encoderName: null);
            StatusText = ClipStudioStatusText.EncoderDetectionFailed(ex.Message);
        }
    }

    // ---- 播放控制 ----

    public void PlayPause()
    {
        if (ClipSelectionLogic.ShouldRestartFromStart(_duration, _position, _isPlaying))
        {
            SeekTo(TimeSpan.Zero);
        }

        PlayPauseRequested?.Invoke();
    }

    public void SeekBy(TimeSpan delta) => SeekTo(_position + delta);

    public void SeekRelative(bool forward, bool fine)
    {
        SeekTo(_position + ClipSelectionLogic.SeekOffset(forward, fine));
    }

    public void SeekTo(TimeSpan position)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        // 边录边剪：播放头只允许在安全可剪范围内操作，避免读到尚未写入文件的尾部。
        var upperBound = ClipSelectionLogic.UpperBound(IsLiveSession, SafeClipDuration, _duration);
        position = ClipStudioPresentationLogic.Clamp(position, TimeSpan.Zero, upperBound);
        Position = position;
        if (_danmakuPlaybackEnabled && _cursor is not null)
        {
            var batch = _cursor.SeekTo((long)position.TotalMilliseconds, DanmakuPlaybackCursor.DefaultLookbackMs);
            DanmakuSyncRequested?.Invoke(ClipStudioPresentationLogic.ToDanmakuMessages(batch, _currentSession?.Platform ?? PlatformType.Bilibili, _currentSession?.RoomId ?? "local-clip", DateTimeOffset.UtcNow), true);
        }

        SeekRequested?.Invoke(position);
    }

    /// <summary>播放器每 100ms 回报当前位置：推进弹幕游标并批量推送新弹幕。</summary>
    public void OnPlayerPositionChanged(TimeSpan position)
    {
        if (_duration > TimeSpan.Zero)
        {
            position = ClipStudioPresentationLogic.Clamp(position, TimeSpan.Zero, _duration);
        }

        Position = position;
        if (!_danmakuPlaybackEnabled || _cursor is null)
        {
            return;
        }

        var batch = _cursor.AdvanceTo((long)position.TotalMilliseconds);
        if (batch.Count > 0)
        {
            DanmakuSyncRequested?.Invoke(ClipStudioPresentationLogic.ToDanmakuMessages(batch, _currentSession?.Platform ?? PlatformType.Bilibili, _currentSession?.RoomId ?? "local-clip", DateTimeOffset.UtcNow), false);
        }
    }

    public void OnPlayerStateChanged(bool isPlaying)
    {
        IsPlaying = isPlaying;
    }

    public void OnPlayerCompleted()
    {
        IsPlaying = false;
        Position = _duration;
    }

    public void OnPlayerFailed(string message)
    {
        IsPlaying = false;
        StatusText = ClipStudioStatusText.PlaybackFailed(message);
    }

    public void SuspendPlayback()
    {
        if (_isPlaying)
        {
            PauseRequested?.Invoke();
        }
    }

    private void ReplayDanmakuAroundPosition()
    {
        if (_cursor is null)
        {
            return;
        }

        var batch = _cursor.SeekTo((long)_position.TotalMilliseconds, DanmakuPlaybackCursor.DefaultLookbackMs);
        DanmakuSyncRequested?.Invoke(ClipStudioPresentationLogic.ToDanmakuMessages(batch, _currentSession?.Platform ?? PlatformType.Bilibili, _currentSession?.RoomId ?? "local-clip", DateTimeOffset.UtcNow), true);
    }

    // ---- 选区与高能跳转 ----

    /// <summary>时间轴拖拽/键盘设点后统一收敛：夹到 [0,duration] 并保证最小 0.2 秒选区。</summary>
    public void UpdateSelection(TimeSpan inPoint, TimeSpan outPoint)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        // 边录边剪：入出点都钳制在安全可剪截止时间以内（最后 5 秒不参与导出）。
        var upperBound = ClipSelectionLogic.UpperBound(IsLiveSession, SafeClipDuration, _duration);
        var bounds = ClipSelectionLogic.NormalizeSelection(inPoint, outPoint, upperBound);
        inPoint = bounds.InPoint;
        outPoint = bounds.OutPoint;

        if (_inPoint == inPoint && _outPoint == outPoint)
        {
            return;
        }

        _inPoint = inPoint;
        _outPoint = outPoint;
        OnPropertyChanged(nameof(InPoint));
        OnPropertyChanged(nameof(OutPoint));
        RaiseSelectionChanged();

        if (_currentVideoPath is not null)
        {
            Export.UpdateGeneratedOutputFileName(_currentVideoPath, _inPoint, _outPoint);
        }
    }

    public void SetInFromPosition()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var inPoint = ClipSelectionLogic.InPointFromPosition(_position, _outPoint, _duration);

        UpdateSelection(inPoint, _outPoint);
        RaiseTimelineChanged();
    }

    public void SetOutFromPosition()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var outPoint = ClipSelectionLogic.OutPointFromPosition(_position, _inPoint, _duration);

        UpdateSelection(_inPoint, outPoint);
        RaiseTimelineChanged();
    }

    [RelayCommand]
    private void JumpToHighlight(ClipHighlightItemViewModel? item)
    {
        if (item is null || _duration <= TimeSpan.Zero)
        {
            return;
        }

        foreach (var highlight in Highlights)
        {
            highlight.IsSelected = ReferenceEquals(highlight, item);
        }

        // 高能片段窗口（前 10s / 后 20s）由 ASR 子视图模型统一定义，跳转选区复用同一窗口。
        var window = ClipSelectionLogic.HighlightWindow(item.Position, _duration, ClipAsrViewModel.HighlightLead, ClipAsrViewModel.HighlightTail);
        UpdateSelection(window.InPoint, window.OutPoint);
        RaiseTimelineChanged();
        SeekTo(item.Position);
    }

    // ---- 私有辅助 ----

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(InText));
        OnPropertyChanged(nameof(OutText));
        OnPropertyChanged(nameof(SelectionText));
        Export.NotifyAvailabilityChanged();
        Asr.NotifyAvailabilityChanged();
    }

    private void RaiseTimelineChanged() => TimelineDataChanged?.Invoke(this, EventArgs.Empty);

    private static void ReplaceCancellationSource(ref CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
            source?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        source = new CancellationTokenSource();
    }

    // ---- IClipAsrHost：ASR 子视图模型读取的剪辑台边界 ----

    string? IClipAsrHost.CurrentVideoPath => _currentVideoPath;

    TimeSpan IClipAsrHost.Duration => _duration;

    TimeSpan IClipAsrHost.InPoint => _inPoint;

    TimeSpan IClipAsrHost.OutPoint => _outPoint;

    bool IClipAsrHost.HasVideo => HasVideo;

    TimeSpan IClipAsrHost.SelectionMinimum => ClipSelectionLogic.MinSelection;

    void IClipAsrHost.SeekTo(TimeSpan position) => SeekTo(position);

    void IClipAsrHost.SetStatusText(string text) => StatusText = text;

    void IClipAsrHost.RefreshHighlightAvailability() => RefreshHighlightTranscribeAvailability();

    // ---- IClipExportHost：导出子视图模型读取的剪辑台边界 ----

    string? IClipExportHost.CurrentVideoPath => _currentVideoPath;

    bool IClipExportHost.HasVideo => HasVideo;

    TimeSpan IClipExportHost.InPoint => _inPoint;

    TimeSpan IClipExportHost.OutPoint => _outPoint;

    TimeSpan IClipExportHost.SelectionMinimum => ClipSelectionLogic.MinSelection;

    bool IClipExportHost.IsLiveSession => IsLiveSession;

    TimeSpan IClipExportHost.SafeClipDuration => SafeClipDuration;

    IReadOnlyList<RecordedDanmakuItem>? IClipExportHost.DanmakuItems => _danmakuItems;

    string? IClipExportHost.DanmakuPath => _currentDanmakuPath;

    string IClipExportHost.SourceVideoCodec => _sourceVideoCodec;

    IClipExporter IClipExportHost.Exporter => _exporter;

    string IClipExportHost.EncoderText => _encoderText;

    void IClipExportHost.SetStatusText(string text) => StatusText = text;

    void IClipExportHost.SetSelection(TimeSpan start, TimeSpan end) => UpdateSelection(start, end);

    /// <summary>按全局互斥状态刷新每张高能卡片「🎙 转写」按钮的可用性。</summary>
    private void RefreshHighlightTranscribeAvailability()
    {
        foreach (var highlight in Highlights)
        {
            highlight.CanTranscribe = !Asr.IsRunning;
        }
    }

    /// <summary>打开视频/场次时同步清空高能卡片的识别结果与「转写中」状态（面板由 <see cref="ClipAsrViewModel.Reset"/> 清理）。</summary>
    private void ResetHighlightAsrState()
    {
        foreach (var highlight in Highlights)
        {
            highlight.AsrResult = null;
            highlight.AsrError = null;
            highlight.IsTranscribing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _liveSync.StopAutoSync();
        try
        {
            _liveRecordings.SessionCompleted -= OnLiveRecordingSessionCompleted;
        }
        catch
        {
            // 事件源可能已释放，忽略。
        }

        try
        {
            _loadCts?.Cancel();
            _waveformCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loadCts?.Dispose();
        _waveformCts?.Dispose();
        _loadCts = null;
        _waveformCts = null;
        _liveSync.Dispose();
        Asr.Dispose();
        Export.Dispose();
        Highlights.Clear();
        Sessions.Clear();
    }
    /// <summary>把同步循环需要的 UI / 数据操作显式暴露给 <see cref="LiveSessionCoordinator"/>。</summary>
    private sealed class LiveSessionSyncHost : ILiveSessionSyncHost
    {
        private readonly ClipStudioViewModel _owner;

        public LiveSessionSyncHost(ClipStudioViewModel owner) => _owner = owner;

        public bool IsDisposed => _owner._disposed;

        public bool IsAutoSyncEnabled => _owner.IsAutoSyncEnabled;

        public RecordingSession? CurrentSession => _owner._currentSession;

        public string? DanmakuPath => _owner._currentDanmakuPath;

        public TimeSpan LiveRecordedDuration => _owner.LiveRecordedDuration;

        public TimeSpan SafeClipDuration => _owner.SafeClipDuration;

        public void ApplyProgress(LiveRecordingInfo latest)
        {
            _owner.UpdateLiveClipProgress(latest);
            _owner.Duration = _owner.LiveRecordedDuration;

            // 播放头不能停在安全区之外（导出/seek 都以安全截止为上限）。
            var maxPosition = _owner.SafeClipDuration > TimeSpan.Zero
                ? _owner.SafeClipDuration
                : _owner.LiveRecordedDuration;
            if (_owner.Position > maxPosition)
            {
                _owner.Position = maxPosition;
            }
        }

        public void ApplyDanmaku(IReadOnlyList<RecordedDanmakuItem> items)
        {
            var merged = new List<RecordedDanmakuItem>(_owner.DanmakuItemCount + items.Count);
            if (_owner._danmakuItems is not null)
            {
                merged.AddRange(_owner._danmakuItems);
            }

            merged.AddRange(items);
            _owner._danmakuItems = merged;
            _owner._cursor?.Append(items);
            _owner.OnPropertyChanged(nameof(HasDanmaku));
            _owner.OnPropertyChanged(nameof(DanmakuInfoText));
            _owner.OnPropertyChanged(nameof(DanmakuItemCount));
        }

        public void ApplyWaveform() => _owner.RebuildLiveWaveformData();

        public void SetWaveformLoading(bool loading) => _owner.IsWaveformLoading = loading;

        public async Task ReloadHighlightsAsync(TimeSpan duration, CancellationToken ct)
        {
            if (_owner._currentSession is not { } session)
            {
                return;
            }

            _owner.Highlights.Clear();
            _owner.HighlightMarkers = Array.Empty<ClipHighlightMarker>();
            await _owner.LoadHighlightsAsync(session, duration, ct).ConfigureAwait(true);
        }

        public void RequestPlayerReload() => _owner.LiveReloadRequested?.Invoke();

        public void RaiseTimelineChanged() => _owner.RaiseTimelineChanged();

        public void SetStatus(string text) => _owner.StatusText = text;

        public void SetSyncing(bool syncing) => _owner.OnLiveSyncingChanged(syncing);

        public void StopForSegmentedRecording(int partNumber)
        {
            _owner.ExitLiveMode();
            _owner.RaiseTimelineChanged();
            _owner.StatusText = ClipStudioStatusText.LiveSyncStoppedBySegment(partNumber);
        }

        public Task HandleRecordingCompletedAsync()
            => _owner._currentSession is { } session
                ? _owner.OnLiveSessionCompletedAsync(session)
                : Task.CompletedTask;
    }

    /// <summary>同步开始/结束通知：刷新命令可用性与「同步中」指示。</summary>
    private void OnLiveSyncingChanged(bool syncing)
    {
        OnPropertyChanged(nameof(IsSyncingLive));
        SyncLiveProgressCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把「打开 live 会话」需要的 UI / 数据操作显式暴露给 <see cref="LiveSessionCoordinator"/>。</summary>
    private sealed class LiveSessionOpenHost : ILiveSessionOpenHost
    {
        private readonly ClipStudioViewModel _owner;

        public LiveSessionOpenHost(ClipStudioViewModel owner) => _owner = owner;

        public LiveSessionLoadHandle BeginLoad()
        {
            _owner._loadVersion++;
            var version = _owner._loadVersion;
            ReplaceCancellationSource(ref _owner._loadCts);
            ReplaceCancellationSource(ref _owner._waveformCts);
            return new LiveSessionLoadHandle(version, _owner._loadCts!.Token, _owner._waveformCts!.Token);
        }

        public bool IsLoadCurrent(LiveSessionLoadHandle handle)
            => handle.Version == _owner._loadVersion && !_owner._disposed;

        public void SetBusy(bool busy) => _owner.IsBusy = busy;

        public void SetStatus(string text) => _owner.StatusText = text;

        public void PrepareLiveSessionFields(RecordingSession session, string partPath, string? danmakuPath)
        {
            _owner._currentSession = session;
            _owner._currentVideoPath = partPath;
            _owner._currentDanmakuPath = danmakuPath;
            _owner.Asr.Reset();
            _owner.ResetHighlightAsrState();
        }

        public void SetLiveVideoTitle(string title) => _owner.VideoTitle = title;

        public async Task<VideoMetadata?> ProbeVideoAsync(string path, CancellationToken ct)
            => await _owner._videoProbe.ProbeAsync(path, ct).ConfigureAwait(true);

        public void EnterLiveSession(
            RecordingSession session,
            LiveRecordingInfo liveInfo,
            string partPath,
            VideoMetadata? metadata)
        {
            // 探测成功时沿用真实编码（hevc 无损导出需要打 hvc1 tag）；失败时按 h264 兜底。
            _owner._sourceVideoCodec = string.IsNullOrWhiteSpace(metadata?.VideoCodec)
                ? "h264"
                : metadata!.VideoCodec;

            _owner.StartLiveClipSession(liveInfo, partPath);
            _owner.Duration = _owner.LiveRecordedDuration;
            _owner.VideoSubtitle = ClipStudioPresentationLogic.BuildLiveVideoSubtitle(session, liveInfo, metadata);
            _owner.Position = TimeSpan.Zero;
            _owner.UpdateSelection(
                TimeSpan.Zero,
                _owner.SafeClipDuration > TimeSpan.Zero ? _owner.SafeClipDuration : _owner.LiveRecordedDuration);
            _owner.Highlights.Clear();
            _owner.HighlightMarkers = Array.Empty<ClipHighlightMarker>();
            _owner.Waveform = null;
            _owner._danmakuItems = Array.Empty<RecordedDanmakuItem>();
            _owner._cursor = new DanmakuPlaybackCursor(_owner._danmakuItems);
            _owner._cursor.Reset(0);
            _owner.OnPropertyChanged(nameof(ClipStudioViewModel.HasDanmaku));
            _owner.OnPropertyChanged(nameof(ClipStudioViewModel.DanmakuInfoText));
            _owner.OnPropertyChanged(nameof(ClipStudioViewModel.DanmakuItemCount));
        }

        public Task LoadHighlightsAsync(RecordingSession session, TimeSpan duration, CancellationToken ct)
            => _owner.LoadHighlightsAsync(session, duration, ct);

        public void CompleteLiveOpen(string partPath)
        {
            // 先用占位数组按实时时长生成波形，等增量音频峰值提取完成后再刷新。
            _owner.RebuildLiveWaveformData();
            _owner.Export.SetGeneratedOutputFileName(partPath, _owner._inPoint, _owner._outPoint);
            _owner.IsWaveformLoading = true;
            _owner.RaiseTimelineChanged();
            _owner.DanmakuEnabledChanged?.Invoke(_owner._danmakuPlaybackEnabled);
            _owner.DanmakuSyncRequested?.Invoke(Array.Empty<DanmakuMessage>(), true);
            _owner.LoadLiveVideoRequested?.Invoke(partPath);
            _owner.StatusText = ClipStudioStatusText.LiveOpened;
            _owner.Export.NotifyAvailabilityChanged();
        }
    }

}

/// <summary>倍速下拉项。</summary>
public sealed record ClipSpeedOption(double Value, string DisplayText);
