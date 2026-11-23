using System.Collections.ObjectModel;
using System.Diagnostics;
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
public sealed partial class ClipStudioViewModel : ObservableObject, IDisposable
{
    private const int MaxSessionCount = 100;
    private const int MaxExportTasks = 6;
    private const int MaxHighlightCount = 500;
    private static readonly TimeSpan MinSelection = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan HighlightLead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HighlightTail = TimeSpan.FromSeconds(20);

    private readonly IRecordingSessionRepository _sessions;
    private readonly IHighlightEventRepository _highlights;
    private readonly IAppSettingsRepository _settings;
    private readonly VideoProbeService _videoProbe;
    private readonly IWaveformService _waveforms;
    private readonly IHardwareEncoderDetector _encoders;
    private readonly IClipExporter _exporter;

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

    public ClipStudioViewModel(
        IRecordingSessionRepository sessions,
        IHighlightEventRepository highlights,
        IAppSettingsRepository settings,
        VideoProbeService videoProbe,
        IWaveformService waveforms,
        IHardwareEncoderDetector encoders,
        IClipExporter exporter)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _highlights = highlights ?? throw new ArgumentNullException(nameof(highlights));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _videoProbe = videoProbe ?? throw new ArgumentNullException(nameof(videoProbe));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));

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

    public ObservableCollection<ClipSessionOption> Sessions { get; } = new();

    public ObservableCollection<ClipHighlightItemViewModel> Highlights { get; } = new();

    public ObservableCollection<ClipExportTaskViewModel> ExportTasks { get; } = new();

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
        ? "无伴随弹幕文件"
        : $"弹幕 {_danmakuItems.Count} 条";

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
        private set => SetProperty(ref _isBusy, value);
    }

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
                foreach (var session in sessions)
                {
                    Sessions.Add(new ClipSessionOption(session));
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

        if (string.IsNullOrWhiteSpace(session.VideoFilePath) || !File.Exists(session.VideoFilePath))
        {
            StatusText = $"录像文件不存在，可能已被移动或删除：{session.VideoFilePath}";
            return;
        }

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

        position = Clamp(position, TimeSpan.Zero, _duration);
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

        inPoint = Clamp(inPoint, TimeSpan.Zero, _duration);
        outPoint = Clamp(outPoint, TimeSpan.Zero, _duration);
        if (outPoint - inPoint < MinSelection)
        {
            outPoint = inPoint + MinSelection;
            if (outPoint > _duration)
            {
                outPoint = _duration;
                inPoint = _duration - MinSelection;
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

        var start = item.Position - HighlightLead;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var end = item.Position + HighlightTail;
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
            StatusText = $"打开输出目录失败：{ex.Message}";
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
        Highlights.Clear();
        Sessions.Clear();
        ExportTasks.Clear();
    }
}

/// <summary>倍速下拉项。</summary>
public sealed record ClipSpeedOption(double Value, string DisplayText);
