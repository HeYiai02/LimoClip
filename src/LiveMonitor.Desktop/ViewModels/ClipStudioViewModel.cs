using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Controls;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// V0.4 剪辑台 ViewModel：录像场次/本地文件加载、libmpv 预览控制、弹幕回放同步、
/// 双波形时间轴选区与双版切片导出。所有播放器/控件交互都通过事件交给 View 桥接，
/// 本类不直接引用 Avalonia 控件，保证可独立演进与测试。
/// </summary>
public sealed partial class ClipStudioViewModel : ObservableObject, IDisposable, IClipAsrHost
{
    private const int MaxSessionCount = 100;
    private const int MaxExportTasks = 6;
    private const int MaxHighlightCount = 500;
    private static readonly TimeSpan MinSelection = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan LiveSyncInterval = TimeSpan.FromSeconds(10);
    private const int LivePeaksPerSecond = 2;
    private const double LivePeakBucketSeconds = 0.5;

    private readonly IRecordingSessionRepository _sessions;
    private readonly IHighlightEventRepository _highlights;
    private readonly IAppSettingsRepository _settings;
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
    private CancellationTokenSource? _exportCts;

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
    private ClipExportMode _exportMode = ClipExportMode.Both;
    private string _videoTitle = "未加载视频";
    private string _videoSubtitle = "从左侧选择录像场次，或打开本地 mkv / mp4 文件";
    private string _encoderText = "自动探测中…";
    private string _statusText = "从左侧选择录像，或打开本地视频开始剪辑。";
    private string _outputDirectory = string.Empty;
    private string _outputFileName = string.Empty;
    private bool _outputFileNameAuto = true;
    private bool _settingAutoOutputFileName;
    private bool _isWaveformLoading;
    private bool _isPlaying;
    private bool _isBusy;
    private bool _isExporting;
    private bool _danmakuPlaybackEnabled = true;

    // ---- 边录边剪状态 ----
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private bool _isLiveSession;
    private TimeSpan _liveRecordedDuration;
    private TimeSpan _safeClipDuration;
    private string _liveStatusText = string.Empty;
    private bool _isAutoSyncEnabled = true;
    private bool _isSyncingLive;
    private LiveRecordingInfo? _liveInfo;
    private string? _livePartPath;
    private long _liveDanmakuOffset;
    private float[]? _livePeaks;
    private TimeSpan _livePeaksExtractedUntil;
    private DispatcherTimer? _liveSyncTimer;
    private readonly SemaphoreSlim _liveWaveformGate = new(1, 1);

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
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _videoProbe = videoProbe ?? throw new ArgumentNullException(nameof(videoProbe));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _liveRecordings = liveRecordings ?? throw new ArgumentNullException(nameof(liveRecordings));
        _liveWaveforms = liveWaveforms ?? throw new ArgumentNullException(nameof(liveWaveforms));
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

    /// <summary>由 View 注入：选择切片导出目录，返回绝对路径；null 表示取消。</summary>
    public Func<Task<string?>>? PickExportDirectoryAsync { get; set; }

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

    public ObservableCollection<ClipExportTaskViewModel> ExportTasks { get; } = new();

    /// <summary>剪辑台「语音识别」子视图模型：面板状态、命令与结果行统一由它维护。</summary>
    public ClipAsrViewModel Asr { get; }

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

    public string DanmakuInfoText => _danmakuItems is null
        ? (IsLiveSession ? "弹幕录制中…" : "无伴随弹幕文件")
        : $"弹幕 {_danmakuItems.Count} 条" + (IsLiveSession ? " · 录制中" : string.Empty);

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
            RaiseExportAvailability();
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

    public string DurationText => ClipExportTaskViewModel.FormatTime(_duration);

    public string PositionText => $"{ClipExportTaskViewModel.FormatTime(_position)} / {DurationText}";

    public string InText => ClipExportTaskViewModel.FormatTime(_inPoint);

    public string OutText => ClipExportTaskViewModel.FormatTime(_outPoint);

    public string SelectionText => _duration > TimeSpan.Zero
        ? ClipExportTaskViewModel.FormatTime(_outPoint - _inPoint)
        : "00:00";

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

    public string PlayPauseGlyph => _isPlaying ? "⏸" : "▶";

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
        private set => SetProperty(ref _encoderText, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetProperty(ref _outputDirectory, value))
            {
                RaiseExportAvailability();
            }
        }
    }

    public string OutputFileName
    {
        get => _outputFileName;
        set
        {
            if (!SetProperty(ref _outputFileName, value))
            {
                return;
            }

            if (!_settingAutoOutputFileName)
            {
                // 用户手动改过文件名后，不再随选区自动覆盖。
                _outputFileNameAuto = false;
            }

            RaiseExportAvailability();
        }
    }

    public bool IsCleanOnlyExport
    {
        get => _exportMode == ClipExportMode.CleanOnly;
        set => SetExportMode(ClipExportMode.CleanOnly, value);
    }

    public bool IsDanmakuOnlyExport
    {
        get => _exportMode == ClipExportMode.BurnedDanmaku;
        set => SetExportMode(ClipExportMode.BurnedDanmaku, value);
    }

    public bool IsBothExport
    {
        get => _exportMode == ClipExportMode.Both;
        set => SetExportMode(ClipExportMode.Both, value);
    }

    public ClipExportMode ExportMode => _exportMode;

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                RaiseExportAvailability();
                OnPropertyChanged(nameof(ExportButtonText));
            }
        }
    }

    public string ExportButtonText => _isExporting ? "导出中…" : "开始导出 MP4";

    public bool CanExport =>
        HasVideo && !IsExporting
        && _outPoint - _inPoint >= MinSelection
        && !string.IsNullOrWhiteSpace(_outputDirectory)
        && !string.IsNullOrWhiteSpace(_outputFileName);

    /// <summary>关键帧对齐误差提示（无损切割可能提前 1~3 秒）。</summary>
    public string KeyframeTip => "无弹幕版使用 -c copy 无损切割，入点会自动对齐到最近关键帧，可能提前 1~3 秒。";

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
    public bool IsLiveSession
    {
        get => _isLiveSession;
        private set
        {
            if (!SetProperty(ref _isLiveSession, value))
            {
                return;
            }

            SyncLiveProgressCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(DanmakuInfoText));
        }
    }

    /// <summary>录制中分片已录制的实时时长（含仍在前端写入、尚不可安全剪裁的尾部）。</summary>
    public TimeSpan LiveRecordedDuration
    {
        get => _liveRecordedDuration;
        private set => SetProperty(ref _liveRecordedDuration, value);
    }

    /// <summary>安全可剪截止时间（已录时长 - 5 秒安全缓冲）；Zero 表示暂不可剪。</summary>
    public TimeSpan SafeClipDuration
    {
        get => _safeClipDuration;
        private set
        {
            if (SetProperty(ref _safeClipDuration, value))
            {
                LiveSafeDurationChanged?.Invoke();
            }
        }
    }

    /// <summary>录制中状态条文案（含已录时长与安全可剪截止）。</summary>
    public string LiveStatusText
    {
        get => _liveStatusText;
        private set => SetProperty(ref _liveStatusText, value);
    }

    /// <summary>是否每 10 秒自动同步高能/弹幕（不重载播放器、不打断播放）。</summary>
    public bool IsAutoSyncEnabled
    {
        get => _isAutoSyncEnabled;
        set => SetProperty(ref _isAutoSyncEnabled, value);
    }

    /// <summary>是否正在执行同步（防止手动/自动同步重入）。</summary>
    public bool IsSyncingLive
    {
        get => _isSyncingLive;
        private set
        {
            if (SetProperty(ref _isSyncingLive, value))
            {
                SyncLiveProgressCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanSyncLiveProgress => IsLiveSession && !IsBusy && !IsSyncingLive;

    // ---- 初始化 / 场次 ----

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        OutputDirectory = await ResolveDefaultOutputDirectoryAsync(ct).ConfigureAwait(true);
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
            StatusText = $"加载录像列表失败：{ex.Message}";
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
            StatusText = $"录像文件不存在，可能已被移动或删除：{session.VideoFilePath}";
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
            StatusText = "当前界面未接入本地文件选择器。";
            return;
        }

        string? path;
        try
        {
            path = await PickVideoFileAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"选择文件失败：{ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenVideoCoreAsync(path, FindCompanionDanmaku(path), session: null).ConfigureAwait(true);
    }

    private async Task OpenVideoCoreAsync(string videoPath, string? danmakuPath, RecordingSession? session)
    {
        if (!File.Exists(videoPath))
        {
            StatusText = $"视频文件不存在：{videoPath}";
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
        StatusText = "正在读取视频信息…";
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
            VideoSubtitle = BuildVideoSubtitle(videoPath, session, metadata);

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
                    StatusText = $"弹幕文件读取失败（仍可导出无弹幕版）：{ex.Message}";
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

            SetAutoOutputFileName(BuildDefaultOutputName(videoPath, _inPoint, _outPoint));
            IsWaveformLoading = true;
            RaiseTimelineChanged();
            DanmakuEnabledChanged?.Invoke(_danmakuPlaybackEnabled);
            DanmakuSyncRequested?.Invoke(Array.Empty<DanmakuMessage>(), true);
            LoadVideoRequested?.Invoke(videoPath);
            StatusText = $"已加载 {Path.GetFileName(videoPath)}（{ClipExportTaskViewModel.FormatTime(metadata.Duration)}）";
            RaiseExportAvailability();

            _ = LoadWaveformAsync(videoPath, danmakuPath, metadata.Duration, waveformCt);
        }
        catch (OperationCanceledException)
        {
            // 用户连续切换录像时取消上一次加载，静默忽略。
        }
        catch (Exception ex)
        {
            StatusText = $"加载视频失败：{ex.Message}";
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

    /// <summary>
    /// 打开「录制中」的场次：读取正在写入的分片文件并进入 live 剪辑模式。
    /// 录制仍在前端追加 mkv cluster，因此只开放到 <see cref="SafeClipDuration"/> 的安全可剪范围。
    /// </summary>
    private async Task OpenLiveSessionAsync(RecordingSession session, LiveRecordingInfo liveInfo)
    {
        if (liveInfo.PartNumber > 1)
        {
            StatusText = $"本次录制已发生自动分段（第 {liveInfo.PartNumber} 段），边录边剪暂不可用，请等待录制结束后再剪辑。";
            return;
        }

        _loadVersion++;
        var version = _loadVersion;
        ReplaceCancellationSource(ref _loadCts);
        ReplaceCancellationSource(ref _waveformCts);
        var ct = _loadCts!.Token;
        var waveformCt = _waveformCts!.Token;

        IsBusy = true;
        StatusText = "正在读取录制中的分片…";
        try
        {
            var partPath = liveInfo.PartFilePath;
            if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
            {
                StatusText = $"录制分片尚未生成，请稍后再试：{partPath}";
                return;
            }

            var danmakuPath = !string.IsNullOrWhiteSpace(session.DanmakuFilePath)
                              && File.Exists(session.DanmakuFilePath)
                ? session.DanmakuFilePath
                : null;

            _currentSession = session;
            _currentVideoPath = partPath;
            _currentDanmakuPath = danmakuPath;
            _liveInfo = liveInfo;
            _livePartPath = partPath;
            _liveDanmakuOffset = 0;
            _livePeaks = null;
            _livePeaksExtractedUntil = TimeSpan.Zero;
            Asr.Reset();
            ResetHighlightAsrState();
            VideoTitle = $"{session.RoomDisplayName} · 录制中";

            // 录制中的分片时长通常是 N/A：探测失败是预期路径，用实时进度兜底。
            VideoMetadata? metadata = null;
            try
            {
                metadata = await _videoProbe.ProbeAsync(partPath, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 分片尚未写完索引 / duration 为 N/A，忽略并按实时进度继续。
            }

            if (version != _loadVersion || _disposed)
            {
                return;
            }

            // 探测成功时沿用真实编码（hevc 无损导出需要打 hvc1 tag）；失败时按 h264 兜底。
            _sourceVideoCodec = string.IsNullOrWhiteSpace(metadata?.VideoCodec) ? "h264" : metadata!.VideoCodec;

            IsLiveSession = true;
            LiveRecordedDuration = liveInfo.RecordedDuration;
            SafeClipDuration = LiveClipSafety.ComputeSafeDuration(liveInfo.RecordedDuration);
            Duration = LiveRecordedDuration;
            VideoSubtitle = BuildLiveVideoSubtitle(session, liveInfo, metadata);
            Position = TimeSpan.Zero;
            UpdateSelection(TimeSpan.Zero, SafeClipDuration > TimeSpan.Zero ? SafeClipDuration : LiveRecordedDuration);
            Highlights.Clear();
            HighlightMarkers = Array.Empty<ClipHighlightMarker>();
            Waveform = null;
            _danmakuItems = Array.Empty<RecordedDanmakuItem>();
            _cursor = new DanmakuPlaybackCursor(_danmakuItems);
            _cursor.Reset(0);
            OnPropertyChanged(nameof(HasDanmaku));
            OnPropertyChanged(nameof(DanmakuInfoText));
            OnPropertyChanged(nameof(DanmakuItemCount));

            if (danmakuPath is not null)
            {
                await RefreshLiveDanmakuAsync(ct).ConfigureAwait(true);
                if (version != _loadVersion || _disposed)
                {
                    return;
                }
            }

            await LoadHighlightsAsync(session, LiveRecordedDuration, ct).ConfigureAwait(true);
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            // 先用占位数组按实时时长生成波形，等增量音频峰值提取完成后再刷新。
            RebuildLiveWaveformData();
            SetAutoOutputFileName(BuildDefaultOutputName(partPath, _inPoint, _outPoint));
            IsWaveformLoading = true;
            LiveStatusText = BuildLiveStatusText();
            RaiseTimelineChanged();
            DanmakuEnabledChanged?.Invoke(_danmakuPlaybackEnabled);
            DanmakuSyncRequested?.Invoke(Array.Empty<DanmakuMessage>(), true);
            LoadLiveVideoRequested?.Invoke(partPath);
            StatusText = "正在录制中，可对已录制部分进行剪辑。";
            RaiseExportAvailability();
            StartLiveSyncTimer();
            _ = ExtractLiveWaveformAsync(waveformCt);
        }
        catch (OperationCanceledException)
        {
            // 切换场次时取消上一次加载，静默忽略。
        }
        catch (Exception ex)
        {
            StatusText = $"打开录制中分片失败：{ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    private static string BuildLiveVideoSubtitle(RecordingSession session, LiveRecordingInfo liveInfo, VideoMetadata? metadata)
    {
        if (metadata is not null && metadata.Width > 0 && metadata.Height > 0)
        {
            var codec = string.IsNullOrWhiteSpace(metadata.VideoCodec) ? "h264" : metadata.VideoCodec.ToUpperInvariant();
            return $"{session.RoomDisplayName} · 录制中 · 分片 {liveInfo.PartNumber} · {metadata.Width}x{metadata.Height} · {metadata.Fps:0.##}fps · {codec}";
        }

        return $"{session.RoomDisplayName} · 录制中 · 分片 {liveInfo.PartNumber}（{FormatFileSize(liveInfo.FileSizeBytes)}）";
    }

    /// <summary>退出 live 模式（切到离线视频 / 录制结束时重置定时器与安全截止）。</summary>
    private void ExitLiveMode()
    {
        StopLiveSyncTimer();
        IsLiveSession = false;
        SafeClipDuration = TimeSpan.Zero;
        LiveRecordedDuration = TimeSpan.Zero;
        LiveStatusText = string.Empty;
        _liveInfo = null;
        _livePartPath = null;
        _liveDanmakuOffset = 0;
        _livePeaks = null;
        _livePeaksExtractedUntil = TimeSpan.Zero;
    }

    /// <summary>增量读取新弹幕（按 byte offset），并同步追加到回放游标。</summary>
    private async Task RefreshLiveDanmakuAsync(CancellationToken ct)
    {
        if (_currentDanmakuPath is null)
        {
            return;
        }

        try
        {
            var result = await DanmakuJsonlReader
                .ReadIncrementalAsync(_currentDanmakuPath, _liveDanmakuOffset, ct)
                .ConfigureAwait(true);
            _liveDanmakuOffset = result.NextByteOffset;
            if (result.NewItems.Count == 0)
            {
                return;
            }

            var merged = new List<RecordedDanmakuItem>(DanmakuItemCount + result.NewItems.Count);
            if (_danmakuItems is not null)
            {
                merged.AddRange(_danmakuItems);
            }

            merged.AddRange(result.NewItems);
            _danmakuItems = merged;
            _cursor?.Append(result.NewItems);
            OnPropertyChanged(nameof(HasDanmaku));
            OnPropertyChanged(nameof(DanmakuInfoText));
            OnPropertyChanged(nameof(DanmakuItemCount));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            // 录制刚开始时 jsonl 可能尚未创建，忽略即可。
        }
        catch (Exception ex)
        {
            StatusText = $"增量读取弹幕失败（不影响导出）：{ex.Message}";
        }
    }

    /// <summary>边录边剪音频波形：只提取上次截止点之后新增的安全区段，追加到固定分辨率峰值数组。</summary>
    private async Task ExtractLiveWaveformAsync(CancellationToken ct)
    {
        // 初始加载与手动同步可能同时触发提取：串行化，避免重复追加同一段峰值。
        var entered = false;
        try
        {
            await _liveWaveformGate.WaitAsync(ct).ConfigureAwait(true);
            entered = true;

            if (ct.IsCancellationRequested || _disposed || !IsLiveSession || _livePartPath is null)
            {
                return;
            }

            // 提取终点对齐到 0.5s 桶网格：每段返回的峰值数严格等于时间轴桶数，
            // 直接首尾拼接才不会出现亚 0.5s 的相位漂移。
            var toBucket = (int)Math.Floor(SafeClipDuration.TotalSeconds * LivePeaksPerSecond);
            var to = TimeSpan.FromSeconds(toBucket / (double)LivePeaksPerSecond);
            var from = _livePeaksExtractedUntil;
            if (to <= from)
            {
                return;
            }

            var peaks = await _liveWaveforms
                .ExtractPeaksAsync(_livePartPath, from, to, LivePeaksPerSecond, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested || _disposed || !IsLiveSession)
            {
                return;
            }

            var expectedCount = (int)Math.Round((to - from).TotalSeconds * LivePeaksPerSecond);
            AppendLivePeaks(peaks, expectedCount);
            _livePeaksExtractedUntil = to;
            RebuildLiveWaveformData();
            RaiseTimelineChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // 分片暂无可解码音频（录制刚开始 / 无音轨）或窗口销毁时信号量已释放：
            // 按“本次无数据”降级，等下次同步再试。
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"边录边剪波形提取失败（不影响导出）：{ex.Message}";
        }
        finally
        {
            if (entered)
            {
                _liveWaveformGate.Release();
            }

            if (!ct.IsCancellationRequested)
            {
                IsWaveformLoading = false;
            }
        }
    }

    private void AppendLivePeaks(IReadOnlyList<float> peaks, int expectedCount)
    {
        if (expectedCount <= 0)
        {
            return;
        }

        var existing = _livePeaks ?? Array.Empty<float>();
        var merged = new float[existing.Length + expectedCount];
        Array.Copy(existing, merged, existing.Length);

        // 每段长度严格对齐到 0.5s 桶数：多出截断、不足补 0，保证时间轴与音频相位一致。
        var copyCount = Math.Min(peaks.Count, expectedCount);
        for (var i = 0; i < copyCount; i++)
        {
            merged[existing.Length + i] = peaks[i];
        }

        _livePeaks = merged;
    }

    /// <summary>按实时已录时长重建固定 0.5s 分辨率的波形数据（音频峰值 + 弹幕密度）。</summary>
    private void RebuildLiveWaveformData()
    {
        if (!IsLiveSession || LiveRecordedDuration <= TimeSpan.Zero)
        {
            return;
        }

        var bucketCount = Math.Max(1, (int)Math.Ceiling(LiveRecordedDuration.TotalSeconds / LivePeakBucketSeconds));
        var peaks = new float[bucketCount];
        if (_livePeaks is { Length: > 0 } source)
        {
            Array.Copy(source, peaks, Math.Min(source.Length, bucketCount));
        }

        var density = new float[bucketCount];
        var maxPerBucket = 0;
        if (_danmakuItems is { Count: > 0 } items)
        {
            var counts = new int[bucketCount];
            foreach (var item in items)
            {
                var index = (int)(item.OffsetMs / (LivePeakBucketSeconds * 1000));
                if (index < 0 || index >= bucketCount)
                {
                    continue;
                }

                counts[index]++;
                if (counts[index] > maxPerBucket)
                {
                    maxPerBucket = counts[index];
                }
            }

            if (maxPerBucket > 0)
            {
                for (var i = 0; i < bucketCount; i++)
                {
                    density[i] = counts[i] / (float)maxPerBucket;
                }
            }
        }

        Waveform = new WaveformData(LiveRecordedDuration, peaks, density, maxPerBucket);
    }

    /// <summary>手动同步（按钮 / F5）：进度、弹幕、高能、波形全量增量刷新，并重载播放器尾部数据。</summary>
    [RelayCommand(CanExecute = nameof(CanSyncLiveProgress))]
    private async Task SyncLiveProgressAsync()
    {
        if (!IsLiveSession || _liveInfo is null || IsSyncingLive)
        {
            return;
        }

        IsSyncingLive = true;
        try
        {
            await SyncLiveProgressCoreAsync(includeAudioPeaks: true, requestPlayerReload: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"同步录制进度失败：{ex.Message}";
        }
        finally
        {
            IsSyncingLive = false;
        }
    }

    /// <summary>10 秒自动同步：只刷新进度/弹幕/高能/密度，不重载播放器、不打断当前播放。</summary>
    private async Task AutoSyncLiveAsync()
    {
        if (!IsLiveSession || IsSyncingLive || !IsAutoSyncEnabled || _liveInfo is null)
        {
            return;
        }

        IsSyncingLive = true;
        try
        {
            await SyncLiveProgressCoreAsync(includeAudioPeaks: false, requestPlayerReload: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"自动同步录制进度失败：{ex.Message}";
        }
        finally
        {
            IsSyncingLive = false;
        }
    }

    private async Task SyncLiveProgressCoreAsync(bool includeAudioPeaks, bool requestPlayerReload)
    {
        if (!IsLiveSession || _liveInfo is null || _currentSession is null)
        {
            return;
        }

        if (!_liveRecordings.TryGetLiveRecording(_liveInfo.SessionId, out var latest))
        {
            // 同步时发现录制已结束：直接切离线模式。
            await OnLiveSessionCompletedAsync(_currentSession).ConfigureAwait(true);
            return;
        }

        _liveInfo = latest;

        // 录制已进入后续分片：live 源仍是 part001，而时间轴 0 点是 part001 起点，
        // 继续同步会让画面/波形与时间轴错位；直接停止边录边剪并提示等录制结束。
        if (latest.PartNumber > 1)
        {
            ExitLiveMode();
            RaiseTimelineChanged();
            StatusText = $"本次录制已发生自动分段（当前第 {latest.PartNumber} 段），边录边剪已停止，请等待录制结束后再剪辑。";
            return;
        }

        _livePartPath = latest.PartFilePath;
        LiveRecordedDuration = latest.RecordedDuration;
        Duration = LiveRecordedDuration;
        SafeClipDuration = LiveClipSafety.ComputeSafeDuration(LiveRecordedDuration);

        // 播放头不能停在安全区之外（导出/seek 都以安全截止为上限）。
        var maxPosition = SafeClipDuration > TimeSpan.Zero ? SafeClipDuration : LiveRecordedDuration;
        if (Position > maxPosition)
        {
            Position = maxPosition;
        }

        await RefreshLiveDanmakuAsync(CancellationToken.None).ConfigureAwait(true);

        // 高能没有增量查询接口：每次全量拉最近 500 条重建，避免重复标记。
        Highlights.Clear();
        HighlightMarkers = Array.Empty<ClipHighlightMarker>();
        await LoadHighlightsAsync(_currentSession, LiveRecordedDuration, CancellationToken.None).ConfigureAwait(true);

        if (includeAudioPeaks)
        {
            await ExtractLiveWaveformAsync(CancellationToken.None).ConfigureAwait(true);
        }

        RebuildLiveWaveformData();
        RaiseTimelineChanged();
        if (requestPlayerReload)
        {
            LiveReloadRequested?.Invoke();
        }

        LiveStatusText = BuildLiveStatusText();
        StatusText = $"已同步最新录制进度（已录 {FormatLiveClock(LiveRecordedDuration)}，安全可剪至 {FormatLiveClock(SafeClipDuration)}）";
    }

    /// <summary>录制结束：停止实时同步，并把当前分片无缝切换为最终文件 + 离线剪辑模式。</summary>
    public async Task OnLiveSessionCompletedAsync(RecordingSession session)
    {
        if (session is null || _disposed || !IsLiveSession || _currentSession?.Id != session.Id)
        {
            return;
        }

        StopLiveSyncTimer();
        var lastPosition = _position;
        var lastIn = _inPoint;
        var lastOut = _outPoint;
        ExitLiveMode();

        try
        {
            var latest = await _sessions.GetByIdAsync(session.Id).ConfigureAwait(true);
            if (latest is null || string.IsNullOrWhiteSpace(latest.VideoFilePath) || !File.Exists(latest.VideoFilePath))
            {
                StatusText = $"录制已结束，但录像文件不可用：{latest?.ErrorMessage}";
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

            if (lastOut > TimeSpan.Zero && lastOut > lastIn + MinSelection)
            {
                UpdateSelection(lastIn, lastOut);
            }

            SeekTo(lastPosition);
            RaiseTimelineChanged();
            StatusText = "录制已结束，已切换为离线剪辑模式。";
        }
        catch (Exception ex)
        {
            StatusText = $"切换离线剪辑失败：{ex.Message}";
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

    private void StartLiveSyncTimer()
    {
        if (_liveSyncTimer is null)
        {
            try
            {
                var timer = new DispatcherTimer { Interval = LiveSyncInterval };
                timer.Tick += OnLiveSyncTimerTick;
                _liveSyncTimer = timer;
            }
            catch (Exception)
            {
                // 单元测试 / 无 Avalonia 平台线程时无法创建 UI 定时器，跳过自动同步。
                return;
            }
        }

        try
        {
            _liveSyncTimer.Start();
        }
        catch (Exception)
        {
            // 同上，忽略。
        }
    }

    private void StopLiveSyncTimer()
    {
        try
        {
            _liveSyncTimer?.Stop();
        }
        catch (Exception)
        {
            // 释放路径忽略。
        }
    }

    private void OnLiveSyncTimerTick(object? sender, EventArgs e) => _ = AutoSyncLiveAsync();

    private string BuildLiveStatusText() => SafeClipDuration > TimeSpan.Zero
        ? $"🔴 正在录制 · 已录 {FormatLiveClock(LiveRecordedDuration)}（安全可剪至 {FormatLiveClock(SafeClipDuration)}）"
        : $"🔴 正在录制 · 已录 {FormatLiveClock(LiveRecordedDuration)}（安全缓冲生成中）";

    private static string FormatLiveClock(TimeSpan time)
    {
        var ts = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private async Task LoadHighlightsAsync(RecordingSession session, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var events = await _highlights
                .GetByRecordingSessionAsync(session.Id, MaxHighlightCount, ct)
                .ConfigureAwait(true);
            var startOffset = new DateTimeOffset(session.StartedAt.ToUniversalTime().Ticks, TimeSpan.Zero);
            var markers = new List<ClipHighlightMarker>(events.Count);
            foreach (var highlightEvent in events)
            {
                var position = highlightEvent.RecordingOffsetMs is { } offsetMs && offsetMs > 0
                    ? TimeSpan.FromMilliseconds(offsetMs)
                    : highlightEvent.TriggerTime - startOffset;
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }
                else if (position > duration)
                {
                    position = duration;
                }

                Highlights.Add(new ClipHighlightItemViewModel(highlightEvent, position));
                markers.Add(new ClipHighlightMarker(position, highlightEvent.TriggerReason));
            }

            HighlightMarkers = markers;
            RefreshHighlightTranscribeAvailability();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"高能推荐加载失败：{ex.Message}";
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
            StatusText = $"波形提取失败（不影响导出）：{ex.Message}";
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
            EncoderText = encoderName switch
            {
                "h264_nvenc" => "NVENC · NVIDIA 硬件编码",
                "h264_qsv" => "QSV · Intel 硬件编码",
                "h264_amf" => "AMF · AMD 硬件编码",
                _ => "libx264 · 软件编码"
            };
        }
        catch (Exception ex)
        {
            EncoderText = "libx264 · 软件编码";
            StatusText = $"硬件编码器探测失败，已回退软件编码：{ex.Message}";
        }
    }

    // ---- 播放控制 ----

    public void PlayPause()
    {
        if (_duration > TimeSpan.Zero && !_isPlaying && _position >= _duration - TimeSpan.FromMilliseconds(200))
        {
            SeekTo(TimeSpan.Zero);
        }

        PlayPauseRequested?.Invoke();
    }

    public void SeekBy(TimeSpan delta) => SeekTo(_position + delta);

    public void SeekRelative(bool forward, bool fine)
    {
        var step = fine ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        SeekTo(_position + (forward ? step : -step));
    }

    public void SeekTo(TimeSpan position)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        // 边录边剪：播放头只允许在安全可剪范围内操作，避免读到尚未写入文件的尾部。
        var upperBound = IsLiveSession && _safeClipDuration > TimeSpan.Zero ? _safeClipDuration : _duration;
        position = Clamp(position, TimeSpan.Zero, upperBound);
        Position = position;
        if (_danmakuPlaybackEnabled && _cursor is not null)
        {
            var batch = _cursor.SeekTo((long)position.TotalMilliseconds, DanmakuPlaybackCursor.DefaultLookbackMs);
            DanmakuSyncRequested?.Invoke(ConvertToDanmakuMessages(batch), true);
        }

        SeekRequested?.Invoke(position);
    }

    /// <summary>播放器每 100ms 回报当前位置：推进弹幕游标并批量推送新弹幕。</summary>
    public void OnPlayerPositionChanged(TimeSpan position)
    {
        if (_duration > TimeSpan.Zero)
        {
            position = Clamp(position, TimeSpan.Zero, _duration);
        }

        Position = position;
        if (!_danmakuPlaybackEnabled || _cursor is null)
        {
            return;
        }

        var batch = _cursor.AdvanceTo((long)position.TotalMilliseconds);
        if (batch.Count > 0)
        {
            DanmakuSyncRequested?.Invoke(ConvertToDanmakuMessages(batch), false);
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
        StatusText = $"播放失败：{message}";
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
        DanmakuSyncRequested?.Invoke(ConvertToDanmakuMessages(batch), true);
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
        var upperBound = IsLiveSession && _safeClipDuration > TimeSpan.Zero ? _safeClipDuration : _duration;
        inPoint = Clamp(inPoint, TimeSpan.Zero, upperBound);
        outPoint = Clamp(outPoint, TimeSpan.Zero, upperBound);
        if (outPoint - inPoint < MinSelection)
        {
            outPoint = inPoint + MinSelection;
            if (outPoint > upperBound)
            {
                outPoint = upperBound;
                inPoint = upperBound - MinSelection;
                if (inPoint < TimeSpan.Zero)
                {
                    inPoint = TimeSpan.Zero;
                }
            }
        }

        if (_inPoint == inPoint && _outPoint == outPoint)
        {
            return;
        }

        _inPoint = inPoint;
        _outPoint = outPoint;
        OnPropertyChanged(nameof(InPoint));
        OnPropertyChanged(nameof(OutPoint));
        RaiseSelectionChanged();

        if (_outputFileNameAuto && _currentVideoPath is not null)
        {
            SetAutoOutputFileName(BuildDefaultOutputName(_currentVideoPath, _inPoint, _outPoint));
        }
    }

    public void SetInFromPosition()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var inPoint = Clamp(_position, TimeSpan.Zero, _duration);
        if (inPoint > _outPoint - MinSelection)
        {
            inPoint = _outPoint - MinSelection;
        }

        UpdateSelection(inPoint, _outPoint);
        RaiseTimelineChanged();
    }

    public void SetOutFromPosition()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var outPoint = Clamp(_position, TimeSpan.Zero, _duration);
        if (outPoint < _inPoint + MinSelection)
        {
            outPoint = _inPoint + MinSelection;
        }

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
        var start = item.Position - ClipAsrViewModel.HighlightLead;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var end = item.Position + ClipAsrViewModel.HighlightTail;
        if (end > _duration)
        {
            end = _duration;
        }

        UpdateSelection(start, end);
        RaiseTimelineChanged();
        SeekTo(item.Position);
    }

    // ---- 导出 ----

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!CanExport || _currentVideoPath is null)
        {
            StatusText = "请先加载视频、设置有效入出点与输出目录。";
            return;
        }

        // 边录边剪且安全缓冲尚未成型（已录不足 5 秒）：此时导出可能读到半截写入的数据。
        if (IsLiveSession && _safeClipDuration <= TimeSpan.Zero)
        {
            StatusText = "安全缓冲生成中（已录不足 5 秒），请稍后再导出。";
            return;
        }

        // 边录边剪：录制结束前保留 5 秒安全缓冲，导出终点自动钳制到安全截止时间。
        if (IsLiveSession && _safeClipDuration > TimeSpan.Zero)
        {
            if (_inPoint >= _safeClipDuration)
            {
                StatusText = "当前入点已超过安全可剪范围，请把入点移到录制进度稍早的位置。";
                return;
            }

            if (_outPoint > _safeClipDuration)
            {
                UpdateSelection(_inPoint, _safeClipDuration);
                StatusText = "已自动保留 5 秒安全缓冲避免影响录制。";
            }
        }

        var request = new ClipExportRequest(
            _currentVideoPath,
            _danmakuItems is { Count: > 0 } ? _currentDanmakuPath : null,
            _outputDirectory,
            SanitizeFileName(_outputFileName),
            _inPoint,
            _outPoint,
            _exportMode);

        var task = new ClipExportTaskViewModel(request, _danmakuItems, _sourceVideoCodec, _exporter);
        ExportTasks.Insert(0, task);
        while (ExportTasks.Count > MaxExportTasks)
        {
            ExportTasks.RemoveAt(ExportTasks.Count - 1);
        }

        IsExporting = true;
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        StatusText = $"开始导出：{task.RangeText} · {task.ModeText}";
        try
        {
            await task.RunAsync(_exportCts.Token).ConfigureAwait(true);
        }
        finally
        {
            IsExporting = false;
        }

        StatusText = task.IsCompleted
            ? $"导出完成：{task.OutputText}"
            : task.IsCanceled
                ? "导出已取消。"
                : $"导出失败：{task.ErrorMessage}";
    }

    [RelayCommand]
    private void CancelExport() => CancelCurrentExport();

    public void CancelCurrentExport()
    {
        if (_exportCts is null || !_isExporting)
        {
            return;
        }

        StatusText = "正在取消导出…";
        try
        {
            _exportCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand]
    private async Task PickOutputDirectoryAsync()
    {
        if (PickExportDirectoryAsync is null)
        {
            StatusText = "当前界面未接入目录选择器。";
            return;
        }

        try
        {
            var directory = await PickExportDirectoryAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                OutputDirectory = directory;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"选择导出目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory))
        {
            StatusText = "输出目录尚不存在，导出时会自动创建。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_outputDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打开输出目录失败：{ex.Message}。请手动在资源管理器中打开：{_outputDirectory}";
        }
    }

    // ---- 私有辅助 ----

    private async Task<string> ResolveDefaultOutputDirectoryAsync(CancellationToken ct)
    {
        var configured = await _settings
            .GetAsync(AppSettingKeys.RecordingDefaultDirectory, ct)
            .ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured.Trim());
        }

        return RecordingManager.DefaultDirectory;
    }

    private static string BuildVideoSubtitle(string videoPath, RecordingSession? session, VideoMetadata metadata)
    {
        var codec = string.IsNullOrWhiteSpace(metadata.VideoCodec) ? "h264" : metadata.VideoCodec.ToUpperInvariant();
        var geometry = $"{metadata.Width}x{metadata.Height} · {metadata.Fps:0.##}fps · {codec}";
        if (session is null)
        {
            return geometry;
        }

        var title = string.IsNullOrWhiteSpace(session.StreamTitle) ? "本地录像" : session.StreamTitle;
        return $"{session.RoomDisplayName} · {title} · {geometry}";
    }

    private static string BuildDefaultOutputName(string videoPath, TimeSpan start, TimeSpan end)
    {
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var startText = FormatClock(start);
        var endText = FormatClock(end);
        var name = $"{baseName}_{startText}-{endText}";
        return name.Length <= 90 ? name : name[..90];
    }

    private static string FormatClock(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\.mm\.ss")
        : time.ToString(@"mm\.ss");

    /// <summary>本地文件若存在同名 .jsonl 则自动作为弹幕伴随文件。</summary>
    private static string? FindCompanionDanmaku(string videoPath)
    {
        var truncated = Path.ChangeExtension(videoPath, null);
        if (truncated is null)
        {
            return null;
        }

        var candidate = truncated + ".jsonl";
        return File.Exists(candidate) ? candidate : null;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(fileName.Length);
        foreach (var ch in fileName.Trim())
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? $"clip_{DateTime.Now:yyyyMMdd_HHmmss}" : sanitized;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private List<DanmakuMessage> ConvertToDanmakuMessages(IReadOnlyList<RecordedDanmakuItem> items)
    {
        var messages = new List<DanmakuMessage>(items.Count);
        var platform = _currentSession?.Platform ?? PlatformType.Bilibili;
        var roomId = _currentSession?.RoomId ?? "local-clip";
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            messages.Add(new DanmakuMessage(
                platform,
                roomId,
                $"rec-{item.OffsetMs}",
                item.SenderName,
                item.Text,
                now,
                item.IsGift,
                item.IsSticker));
        }

        return messages;
    }

    private void SetExportMode(ClipExportMode mode, bool isChecked)
    {
        if (!isChecked || _exportMode == mode)
        {
            return;
        }

        _exportMode = mode;
        OnPropertyChanged(nameof(IsCleanOnlyExport));
        OnPropertyChanged(nameof(IsDanmakuOnlyExport));
        OnPropertyChanged(nameof(IsBothExport));
    }

    /// <summary>自动填充输出文件名；用户手动改过文件名后由 setter 关闭自动覆盖。</summary>
    private void SetAutoOutputFileName(string name)
    {
        _settingAutoOutputFileName = true;
        try
        {
            OutputFileName = name;
        }
        finally
        {
            _settingAutoOutputFileName = false;
        }

        _outputFileNameAuto = true;
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(InText));
        OnPropertyChanged(nameof(OutText));
        OnPropertyChanged(nameof(SelectionText));
        RaiseExportAvailability();
        Asr.NotifyAvailabilityChanged();
    }

    private void RaiseExportAvailability() => OnPropertyChanged(nameof(CanExport));

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

    TimeSpan IClipAsrHost.SelectionMinimum => MinSelection;

    void IClipAsrHost.SeekTo(TimeSpan position) => SeekTo(position);

    void IClipAsrHost.SetStatusText(string text) => StatusText = text;

    void IClipAsrHost.RefreshHighlightAvailability() => RefreshHighlightTranscribeAvailability();

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
        StopLiveSyncTimer();
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
            _exportCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loadCts?.Dispose();
        _waveformCts?.Dispose();
        _exportCts?.Dispose();
        _loadCts = null;
        _waveformCts = null;
        _exportCts = null;
        Asr.Dispose();
        Highlights.Clear();
        Sessions.Clear();
        ExportTasks.Clear();
    }
}

/// <summary>倍速下拉项。</summary>
public sealed record ClipSpeedOption(double Value, string DisplayText);
