using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ToolTip = Avalonia.Controls.ToolTip;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibMpv.Client;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 单路直播播放器。一个实例对应一个 mpv context，常驻于 Dock 窗格中。
/// 注意：不在 OnDetachedFromVisualTree 里 Dispose；拖拽、方案切换只是把控件
/// 从旧视觉父级解绑，真正销毁由 WorkspacePlayerManager 显式调用 Dispose。
/// </summary>
public partial class MpvPlayerView : Avalonia.Controls.UserControl, IWorkspacePlayer
{
    private static readonly TimeSpan RenderReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartPlaybackTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);

    private static readonly ConditionalWeakTable<MpvContext, string> AudioMeterPaths = new();

    private readonly object _contextGate = new();
    private readonly DispatcherTimer _audioMeterTimer;
    private readonly DispatcherTimer _seekPollTimer;
    private readonly DispatcherTimer _seekCommitTimer;
    private MpvContext? _context;
    private TaskCompletionSource<bool>? _loadTcs;
    private DispatcherTimer? _healthTimer;
    private Task? _disposeTask;
    private string? _audioMeterPath;
    private bool _meterAttached;
    private double _audioMeterLevel;
    private int _disposed;
    private int _suppressVolumeChange;
    private RoomTileViewModel? _boundTile;
    private bool _danmakuMasterEnabled = true;
    private bool _danmakuStickersEnabled = true;
    private bool _danmakuEmoteImagesEnabled = true;

    private PlayerState _state = PlayerState.Offline;
    private PlayerHealthMetrics _metrics = PlayerHealthMetrics.Empty;
    private StreamQuality? _quality;
    private string? _qualityDisplayName;
    private bool _userStopped;
    private bool _muted;
    private bool _paused;
    private DateTimeOffset _suppressEndUntil = DateTimeOffset.MinValue;
    private bool _compact;
    private bool _compactApplied;
    // 音频解码从起播起就保持启用，只用 mute 控制是否出声。
    // 否则每次 aid=no -> auto 都会让 mpv 重建音频输出，出现“点开声没声、
    // 再点一下 fps 才恢复”的现象。
    private bool _audioEnabled = true;
    private int _volume = 100;

    // 回放支持：只有 mpv 报告 seekable=yes 且 duration 有限（如支持 Range 的清流、
    // HLS 点播/带 DVR 窗口的源）才显示进度条；普通直播流不受影响。
    // 探测值换算、拖动防抖与 seek 目标格式等纯逻辑在 ReplaySeekLogic 中，便于单测。
    private readonly ReplaySeekLogic _seekLogic = new();
    private int _suppressSeekChange;

    /// <summary>回放源进度条提示：拖动会 seek。</summary>
    private const string ReplaySeekTip = "拖动回放（仅对支持回放的源生效）";

    /// <summary>直播源进度条提示：进度条固定在最新位置，拖动可回看。</summary>
    private const string LiveSeekTip = "正在直播：进度条停留在最新位置，向左拖动可回看";

    public MpvPlayerView()
    {
        InitializeComponent();
        _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _audioMeterTimer.Tick += (_, _) => UpdateAudioMeter();
        _seekPollTimer = new DispatcherTimer { Interval = ReplaySeekLogic.PollInterval };
        _seekPollTimer.Tick += (_, _) => UpdateSeekBar();
        _seekCommitTimer = new DispatcherTimer { Interval = ReplaySeekLogic.CommitDelay };
        _seekCommitTimer.Tick += (_, _) => CommitSeek();
        ApplyStateVisuals(PlayerState.Offline, RoomStatusText.WaitingForRoom);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            // 必须赶在 VideoView 的 OpenGL 初始化之前挂上 context；
            // VideoView 只会在 OnOpenGlInit 里（GL 上下文当前时）配置 renderer。
            EnsureContext();
        }
        catch
        {
            // 真正错误在 PlayAsync 里呈现。
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // 方案切换/拖拽换位时把控件从旧窗格摘下：这里只解除 VideoView 的挂载，
        // 不 dispose MpvContext，保证流不断；挂到新窗格时 EnsureContext 会重新挂载。
        // 若 detach 事件晚于新窗格的 attach 事件，控件已经重新有视觉根，不能把新挂载清掉。
        if (this.GetVisualRoot() is not null)
        {
            return;
        }

        try
        {
            Video.MpvContext = null;
        }
        catch
        {
            // 旧渲染上下文可能已经随窗口销毁，忽略。
        }
    }

    /// <summary>供 XAML / 视觉树挂载的控件句柄：播放器本身就是 UserControl，返回自身。</summary>
    public Avalonia.Controls.Control? View => this;

    public PlayerState State => _state;

    public StreamQuality? CurrentQuality => _quality;

    public event EventHandler? StateChanged;

    /// <summary>失败或断流后由上层做 3/10/30s 退避重连；播放器自身不解析地址。</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>用户点击“刷新”按钮：由上层立即重新解析地址并重连。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>是否处于暂停状态。</summary>
    public bool IsPaused => _paused;

    /// <summary>暂停 / 继续播放（只影响 mpv 播放，不影响弹幕与录制）。</summary>
    public void TogglePause()
    {
        ThrowIfDisposed();
        SetPaused(!_paused);
    }

    /// <summary>请求上层立即刷新播放（重新取流地址并重连）。</summary>
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void SetPaused(bool paused)
    {
        _paused = paused;
        _context?.SetPropertyFlag("pause", paused);
        PauseIcon.Text = paused ? "\uE768" : "\uE769";
        ToolTip.SetTip(PauseButton, paused ? RoomStatusText.ResumeTip : RoomStatusText.PauseTip);
    }

    /// <summary>
    /// 房间/主播名。展示已统一收敛到窗格左上角标题栏，底栏不再重复显示；
    /// 这里仅保留数据通道（冒烟窗口、焦点浮层等外部设置不丢失）。
    /// </summary>
    public string? RoomTitle { get; set; }

    public string? QualityLabel
    {
        get => QualityText.Text;
        set => QualityText.Text = string.IsNullOrWhiteSpace(value) ? "—" : value;
    }

    public bool AudioEnabled => _audioEnabled;

    public async Task PlayAsync(StreamQuality quality, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quality);
        ThrowIfDisposed();

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => PlayAsyncCore(quality, ct));
            return;
        }

        await PlayAsyncCore(quality, ct);
    }

    private async Task PlayAsyncCore(StreamQuality quality, CancellationToken ct)
    {
        _userStopped = false;
        SetPaused(false);
        _quality = quality;
        _qualityDisplayName = quality.DisplayName;
        // 底栏先只显示档位名；真实帧率等 mpv 报出 container-fps 后再补。
        // 平台元数据里的 Fps 是猜测值（B 站等平台解析不到 60 帧时一律兜底 30），
        // 直接显示会和右上角实测帧率冲突。
        QualityLabel = PlayerQualityText.Format(_qualityDisplayName, 0);
        SetState(PlayerState.Loading, RoomStatusText.OpeningChannel);

        var loadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadTcs = loadTcs;

        try
        {
            var context = EnsureContext();

            using (var renderCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                renderCts.CancelAfter(RenderReadyTimeout);
                await Video.WaitForRenderAsync(renderCts.Token);
            }

            ApplyStreamOptions(context, quality);

            await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(true);

            context.Command("loadfile", quality.StreamUrl, "replace");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StartPlaybackTimeout);
            using var registration = timeout.Token.Register(
                () => loadTcs.TrySetException(new TimeoutException(RoomStatusText.StartTimeout)));

            await loadTcs.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(PlayerState.Offline, RoomStatusText.Canceled);
            throw;
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Failed, Trim(ex.Message, 160));
            throw;
        }
        finally
        {
            if (ReferenceEquals(_loadTcs, loadTcs))
            {
                _loadTcs = null;
            }
        }
    }

    public Task SwitchQualityAsync(StreamQuality quality, CancellationToken ct = default)
        => PlayAsync(quality, ct);

    public Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _userStopped = true;
        _loadTcs?.TrySetCanceled();
        _healthTimer?.Stop();

        var context = _context;
        if (context is not null)
        {
            try
            {
                context.Command("stop");
            }
            catch
            {
                // stop 只是尽量释放流；真正的资源回收在 Dispose。
            }
        }

        SetState(PlayerState.Offline, RoomStatusText.Stopped);
        return Task.CompletedTask;
    }

    public void SetMute(bool muted)
    {
        _muted = muted;
        if (!muted)
        {
            _audioEnabled = true;
        }

        UpdateMuteVisual(muted);

        try
        {
            // 首次开声时才启用音轨解码；之后只切 mute。
            // 若每次静音都设 aid=no，mpv 会拆除音频输出，下一次 aid=auto
            // 需要额外命令才重新初始化，表现为“点开声没声，点一下 fps 才有声”。
            if (!muted)
            {
                _context?.SetPropertyString("aid", "auto");
            }

            _context?.SetPropertyFlag("mute", muted);
        }
        catch
        {
            // mpv 已释放时忽略。
        }

        ApplyAudioMeterState();
    }

    public bool IsMuted => _muted;

    public int Volume => _volume;

    /// <summary>重新布局时先把窗格显示成加载态，避免短暂闪成“未开播”。</summary>
    public void ShowLoading(string? detail = null)
    {
        _userStopped = false;
        SetState(PlayerState.Loading, detail ?? RoomStatusText.Connecting);
    }

    private void UpdateMuteVisual(bool muted)
    {
        MuteIcon.Text = PlayerChromeLogic.MuteIcon(muted);
        Avalonia.Controls.ToolTip.SetTip(MuteButton, PlayerChromeLogic.MuteTip(muted));
    }

    public void SetVolume(int volume)
    {
        _volume = PlayerChromeLogic.ClampVolume(volume);
        _suppressVolumeChange++;
        try
        {
            VolumeSlider.Value = _volume;
        }
        finally
        {
            _suppressVolumeChange--;
        }

        try
        {
            _context?.SetPropertyLong("volume", _volume);
        }
        catch
        {
            // mpv 已释放时忽略。
        }
    }

    public void SetCompactMode(bool enabled)
    {
        _compact = enabled;
        CompactButton.Content = PlayerChromeLogic.CompactButtonText(enabled);
        DanmakuLayer.MaxConcurrent = PlayerChromeLogic.MaxConcurrentFor(enabled);
        ApplyVideoFilter();
    }

    /// <summary>把已通过过滤链的弹幕批量推给渲染层；后台线程也可调用。</summary>
    public void PushDanmaku(IReadOnlyList<DanmakuMessage> messages) => DanmakuLayer.EnqueueRange(messages);

    /// <summary>清空弹幕渲染层（停播、切房或关弹幕时由上层调用）。</summary>
    public void ClearDanmaku() => DanmakuLayer.Clear();

    /// <summary>
    /// 弹幕渲染总闸（设置页全局开关）。关闭时只停渲染，弹幕连接与高能统计不受影响。
    /// 实际可见性 = 总闸 &amp;&amp; 当前窗格的弹幕开关。
    /// </summary>
    public bool DanmakuMasterEnabled
    {
        get => _danmakuMasterEnabled;
        set
        {
            if (_danmakuMasterEnabled == value)
            {
                return;
            }

            _danmakuMasterEnabled = value;
            UpdateDanmakuLayerVisibility();
        }
    }

    /// <summary>
    /// 是否渲染纯大表情包弹幕（设置页开关）。关闭时只跳过渲染，高能统计与连接不受影响。
    /// </summary>
    public bool DanmakuStickersEnabled
    {
        get => _danmakuStickersEnabled;
        set
        {
            if (_danmakuStickersEnabled == value)
            {
                return;
            }

            _danmakuStickersEnabled = value;
            DanmakuLayer.ShowStickers = value;
        }
    }

    /// <summary>
    /// 是否把表情渲染成静态图片（设置页开关，默认关闭）。
    /// 关闭时行内表情显示 [doge] 文本、纯大表情显示 [表情名]。
    /// </summary>
    public bool DanmakuEmoteImagesEnabled
    {
        get => _danmakuEmoteImagesEnabled;
        set
        {
            if (_danmakuEmoteImagesEnabled == value)
            {
                return;
            }

            _danmakuEmoteImagesEnabled = value;
            DanmakuLayer.ShowEmoteImages = value;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachDanmakuTile(DataContext as RoomTileViewModel);
    }

    private void AttachDanmakuTile(RoomTileViewModel? tile)
    {
        if (ReferenceEquals(_boundTile, tile))
        {
            return;
        }

        if (_boundTile is not null)
        {
            _boundTile.PropertyChanged -= OnBoundTilePropertyChanged;
        }

        _boundTile = tile;

        if (_boundTile is not null)
        {
            _boundTile.PropertyChanged += OnBoundTilePropertyChanged;
        }

        UpdateDanmakuLayerVisibility();
    }

    private void OnBoundTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(RoomTileViewModel.IsDanmakuVisible))
        {
            UpdateDanmakuLayerVisibility();
        }
    }

    private void UpdateDanmakuLayerVisibility()
    {
        var visible = PlayerChromeLogic.DanmakuLayerVisible(
            _danmakuMasterEnabled,
            _boundTile?.IsDanmakuVisible == true);
        if (DanmakuLayer.IsVisible == visible)
        {
            return;
        }

        DanmakuLayer.IsVisible = visible;
        if (!visible)
        {
            DanmakuLayer.Clear();
        }
    }

    /// <summary>
    /// V0.1 默认关闭音频解码；焦点窗格需要开声时显式调用。
    /// </summary>
    public void SetAudioEnabled(bool enabled)
    {
        _audioEnabled = enabled;
        _muted = !enabled;
        UpdateMuteVisual(_muted);
        try
        {
            _context?.SetPropertyString("aid", enabled ? "auto" : "no");
            _context?.SetPropertyFlag("mute", !enabled);
        }
        catch
        {
            // mpv 已释放时忽略。
        }

        ApplyAudioMeterState();
    }

    public PlayerHealthMetrics GetHealthMetrics() => _metrics;

    public void ShowOffline(string? detail = null)
    {
        _userStopped = true;
        _healthTimer?.Stop();
        SetState(PlayerState.Offline, detail ?? RoomStatusText.Offline);
    }

    public void ShowFailed(string? detail = null)
    {
        _healthTimer?.Stop();
        SetState(PlayerState.Failed, detail ?? RoomStatusText.PlaybackFailed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _userStopped = true;
        _loadTcs?.TrySetCanceled();
        _healthTimer?.Stop();
        _audioMeterTimer.Stop();
        _seekPollTimer.Stop();
        _seekCommitTimer.Stop();
        _meterAttached = false;
        TryDeleteAudioMeterFile();

        var context = Interlocked.Exchange(ref _context, null);

        void DetachAndDispose()
        {
            try
            {
                Video.MpvContext = null;
            }
            catch
            {
                // 视觉树已销毁时忽略。
            }

            if (context is null)
            {
                return;
            }

            _disposeTask = Task.Run(() =>
            {
                try
                {
                    context.Command("stop");
                }
                catch
                {
                    // 已结束的流可能报错，忽略。
                }

                try
                {
                    context.Dispose();
                }
                catch
                {
                    // 后台释放失败不应拖垮 UI。
                }
            });
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            DetachAndDispose();
        }
        else
        {
            Dispatcher.UIThread.Post(DetachAndDispose);
        }

        AttachDanmakuTile(null);
        StateChanged = null;
        ReconnectRequested = null;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_disposeTask is not null)
        {
            await _disposeTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 方案差量切换时把底层 MpvContext 从旧播放器摘出，控制权交给新播放器；
    /// 不销毁 context，因此流不会断。
    /// </summary>
    public MpvContext? TakeContext()
    {
        lock (_contextGate)
        {
            var context = _context;
            if (context is null)
            {
                return null;
            }

            context.StartFile -= OnStartFile;
            context.FileLoaded -= OnFileLoaded;
            context.EndFile -= OnEndFile;
            _context = null;
            _healthTimer?.Stop();
            _audioMeterTimer.Stop();
            StopSeekPolling();
            _meterAttached = false;
            // 文件交给接管方继续用，旧播放器 Dispose 时不要把文件删掉。
            _audioMeterPath = null;
            _loadTcs?.TrySetCanceled();
            _loadTcs = null;
            Video.MpvContext = null;
            return context;
        }
    }

    /// <summary>
    /// <see cref="IWorkspacePlayer"/> 的会话迁移入口：接管另一个播放器摘出的 MpvContext。
    /// 只有来源同为 <see cref="MpvPlayerView"/> 且确实持有 context 时才成功，
    /// 具体类型校验收敛在实现内部，WorkspacePlayerManager 不再接触具体播放器类型。
    /// </summary>
    public bool TryAdoptSessionFrom(IWorkspacePlayer source, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not MpvPlayerView oldPlayer)
        {
            return false;
        }

        lock (_contextGate)
        {
            if (_context is not null)
            {
                return false;
            }
        }

        var context = oldPlayer.TakeContext();
        if (context is null)
        {
            return false;
        }

        AdoptContext(context, oldPlayer.CurrentQuality, oldPlayer.State, detail);
        return true;
    }

    /// <summary>接管另一个播放器摘出的 MpvContext，保持画面不中断。</summary>
    public void AdoptContext(MpvContext context, StreamQuality? quality, PlayerState state, string? detail)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_contextGate)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("播放器已经持有 MpvContext。");
            }

            _context = context;
            _quality = quality;
            _compactApplied = false;
            if (AudioMeterPaths.TryGetValue(context, out var meterPath))
            {
                _audioMeterPath = meterPath;
                _audioMeterTimer.Start();
            }
            // 摘挂 render context 时 mpv 可能给旧 vo 发一个 -15 的 EndFile；
            // 新宿主刚接管时先忽略这类瞬时错误，避免把仍在播放的流标成失败。
            _suppressEndUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            context.StartFile += OnStartFile;
            context.FileLoaded += OnFileLoaded;
            context.EndFile += OnEndFile;

            context.SetOptionString("aid", _audioEnabled ? "auto" : "no");
            context.SetOptionString("mute", _muted ? "yes" : "no");
            context.SetOptionString("volume", _volume.ToString(CultureInfo.InvariantCulture));
            Video.MpvContext = context;
            ApplyVideoFilter();
            ApplyAudioMeterState();

            if (quality is not null)
            {
                _qualityDisplayName = quality.DisplayName;
                QualityLabel = PlayerQualityText.Format(_qualityDisplayName, 0);
            }

            SetState(state, detail);
            if (state == PlayerState.Playing)
            {
                StartHealthTimer();
            }
        }
    }

    private MpvContext EnsureContext()
    {
        lock (_contextGate)
        {
            if (_context is not null)
            {
                // 从旧视觉父级摘下再挂回时，VideoView 可能已解绑 context；
                // 这里在 GL 初始化前补挂，避免 Dock 拖拽后黑屏。
                if (!ReferenceEquals(Video.MpvContext, _context))
                {
                    Video.MpvContext = _context;
                }

                if (_audioMeterPath is not null)
                {
                    _audioMeterTimer.Start();
                    ApplyAudioMeterState();
                }

                return _context;
            }

            // LibMpv 默认按 version=0 查找 libmpv.dll；本机随包的是 libmpv-2.dll。
            LibMpv.Client.LibMpv.UseLibMpv(2);

            var context = new MpvContext();
            context.SetOptionString("hwdec", "auto-safe");
            context.SetOptionString("vo", "libmpv");
            context.SetOptionString("gpu-hwdec-interop", "auto");
            context.SetOptionString("gpu-api", "opengl");
            context.SetOptionString("cache", "yes");
            context.SetOptionString("demuxer-max-bytes", "4M");
            context.SetOptionString("demuxer-max-back-bytes", "0");
            context.SetOptionString("network-timeout", "10");
            // 多路同时开声时，默认 0.2s 音频缓冲很容易被网络/HLS 分段抖动打穿。
            context.SetOptionString("audio-buffer", "0.8");
            context.SetOptionString("audio-stream-silence", "yes");
            context.SetOptionString("audio-exclusive", "no");
            context.SetOptionString("aid", _audioEnabled ? "auto" : "no");
            context.SetOptionString("mute", _muted ? "yes" : "no");
            context.SetOptionString("volume", _volume.ToString(CultureInfo.InvariantCulture));
            context.SetOptionString("osc", "no");
            context.SetOptionString("osd-level", "0");
            context.SetOptionString("input-default-bindings", "no");
            context.SetOptionString("keep-open", "no");

            _audioMeterPath ??= CreateAudioMeterPath();
            if (_audioMeterPath is not null)
            {
                AudioMeterPaths.AddOrUpdate(context, _audioMeterPath);
                _audioMeterTimer.Start();
                // 静音只关输出，不关解码；电平条必须继续反映音频包络，
                // 否则“静音仍有波动”的既有交互会被破坏。
                if (_audioEnabled)
                {
                    context.SetOptionString("af", AudioMeterLogic.BuildMeterFilter(_audioMeterPath));
                    _meterAttached = true;
                }
            }

            var caFile = ResolveCaFile();
            if (caFile is not null)
            {
                context.SetOptionString("tls-ca-file", caFile);
            }
            else if (string.Equals(Environment.GetEnvironmentVariable("LIVEMONITOR_TLS_NO_VERIFY"), "1", StringComparison.Ordinal))
            {
                context.SetOptionString("tls-verify", "no");
            }

            if (string.Equals(Environment.GetEnvironmentVariable("LIVEMONITOR_MPV_LOG"), "1", StringComparison.Ordinal))
            {
                context.SetOptionString("terminal", "yes");
                context.SetOptionString("msg-level", "all=v");
                context.SetOptionString("log-file", Path.Combine(Path.GetTempPath(), "livemonitor-mpv.log"));
            }

            context.StartFile += OnStartFile;
            context.FileLoaded += OnFileLoaded;
            context.EndFile += OnEndFile;

            _context = context;
            _compactApplied = false;
            Video.MpvContext = context;
            ApplyVideoFilter();
            return context;
        }
    }

    private static string? CreateAudioMeterPath()
    {
        try
        {
            var root = PlayerPathResolver.ResolveAudioMeterRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppContext.BaseDirectory);

            try
            {
                Directory.CreateDirectory(root);
            }
            catch
            {
                root = Path.Combine(AppContext.BaseDirectory, "audio-meter");
                Directory.CreateDirectory(root);
            }

            return Path.Combine(root, $"meter-{Guid.NewGuid():N}.txt");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 音量表跟随音频解码，不跟随 mute：静音时仍要能看到电平波动。
    /// 只有显式关闭音频解码（焦点切换/全静音关闭音频）时才摘掉 astats。
    /// </summary>
    private void ApplyAudioMeterState()
    {
        if (_audioMeterPath is null)
        {
            return;
        }

        if (!_audioEnabled)
        {
            DetachAudioMeter();
        }
        else
        {
            AttachAudioMeter();
        }
    }

    private void AttachAudioMeter()
    {
        if (_audioMeterPath is null || _meterAttached)
        {
            return;
        }

        var context = _context;
        if (context is null)
        {
            // context 尚未创建；EnsureContext 会根据当前 _audioEnabled 状态直接挂 af。
            return;
        }

        try
        {
            context.Command("af", "set", AudioMeterLogic.BuildMeterFilter(_audioMeterPath));
            _meterAttached = true;
            _audioMeterTimer.Start();
        }
        catch
        {
            _meterAttached = false;
        }
    }

    private void DetachAudioMeter()
    {
        _meterAttached = false;
        _audioMeterLevel = 0;
        AudioMeter.Level = 0;

        try
        {
            _context?.Command("af", "clr");
        }
        catch
        {
            // mpv 已释放时忽略。
        }
    }

    private void TryDeleteAudioMeterFile()
    {
        var path = _audioMeterPath;
        _audioMeterPath = null;
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // mpv 可能还持有文件句柄，退出时由系统清理。
        }
    }

    private void UpdateAudioMeter()
    {
        if (!_meterAttached || _audioMeterPath is null || _state != PlayerState.Playing)
        {
            _audioMeterLevel = 0;
            AudioMeter.Level = 0;
            return;
        }

        var target = 0d;
        try
        {
            using var stream = new FileStream(
                _audioMeterPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 0)
            {
                // 只读文件尾，避免长时间运行时把整个日志读进内存。
                if (stream.Length > AudioMeterLogic.MaxTailBytes)
                {
                    stream.Seek(-AudioMeterLogic.MaxTailBytes, SeekOrigin.End);
                }

                using var reader = new StreamReader(stream);
                if (AudioMeterLogic.FindLastRmsDecibels(reader.ReadToEnd()) is { } decibels)
                {
                    target = AudioMeterLogic.MapDecibelsToLevel(decibels);
                }
            }
        }
        catch
        {
            target = 0;
        }

        // 上升快、下降慢，接近真实电平表的观感。
        _audioMeterLevel = AudioMeterLogic.NextLevel(_audioMeterLevel, target);
        AudioMeter.Level = _audioMeterLevel;
    }

    private static string? ResolveCaFile()
    {
        // 发布时把 CA bundle 放成 exe 同目录的 cacert.crt 即可，不需要改环境变量。
        return PlayerPathResolver.ResolveCaFile(
            Environment.GetEnvironmentVariable("LIVEMONITOR_CA_FILE"),
            AppContext.BaseDirectory,
            File.Exists);
    }

    private static void ApplyStreamOptions(MpvContext context, StreamQuality quality)
    {
        // 720P 以上的直播码率明显更高，4M 的 demuxer cache 在 HLS 分段/网络抖动时
        // 容易让音频输出断粮；这里按档位给更宽的缓冲，低清仍保持低内存。
        // 档位选择、默认 UA、可选头清理与 Cookie 拼装等纯逻辑在 StreamOptionMapper 中，便于单测。
        var options = StreamOptionMapper.Build(quality);
        context.SetOptionString("demuxer-max-bytes", options.DemuxerMaxBytes);
        context.SetOptionString("user-agent", options.UserAgent);

        if (options.Referrer is not null)
        {
            context.SetOptionString("referrer", options.Referrer);
        }

        if (options.HttpHeaderFields is not null)
        {
            context.SetOptionString("http-header-fields", options.HttpHeaderFields);
        }

        // YouTube 等平台的直播 HLS 音视频分离：视频轨走 loadfile，音频轨通过 audio-file 附加。
        // 必须显式设置（无音频时清空），否则上一路播放的音轨会残留到下一次。
        try
        {
            context.SetOptionString("audio-file", options.AudioFile ?? string.Empty);
        }
        catch
        {
            // 个别 mpv 版本不支持该选项时退化为纯视频播放，不影响主流程。
        }
    }

    private void ApplyVideoFilter()
    {
        try
        {
            if (_context is null)
            {
                return;
            }

            if (_compact)
            {
                if (!_compactApplied)
                {
                    _context.Command("vf", "add", "@compact:fps=15");
                    _compactApplied = true;
                }
            }
            else if (_compactApplied)
            {
                // 用命名滤镜精确移除，避免空字符串/清空命令在不同 mpv 版本行为不一致。
                _context.Command("vf", "remove", "@compact");
                _compactApplied = false;
            }
        }
        catch
        {
            // mpv 已释放时忽略。
        }
    }

    private void OnStartFile(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_userStopped)
            {
                SetState(PlayerState.Loading, RoomStatusText.Buffering);
            }
        });
    }

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_userStopped)
            {
                return;
            }

            SetState(PlayerState.Playing, null);
            StartHealthTimer();
            _loadTcs?.TrySetResult(true);
        });
    }

    private void OnEndFile(object? sender, MpvEndFileEventArgs e)
    {
        var reason = e.EventData.Reason;
        var error = e.EventData.Error;

        if (reason is MpvEndFileReason.MpvEndFileReasonStop or MpvEndFileReason.MpvEndFileReasonQuit)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_userStopped)
            {
                return;
            }

            if (reason == MpvEndFileReason.MpvEndFileReasonError)
            {
                if (DateTimeOffset.UtcNow < _suppressEndUntil)
                {
                    return;
                }

                var message = RoomStatusText.StreamStartFailed(error);
                SetState(PlayerState.Failed, message);
                _loadTcs?.TrySetException(new InvalidOperationException(message));
                ReconnectRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (reason == MpvEndFileReason.MpvEndFileReasonEof)
            {
                // 直播流被 CDN/网络中断时 mpv 表现为 EOF；这并不代表主播下播，
                // 用「重连中」状态提示用户，同时交给上层退避重连。
                SetState(PlayerState.Reconnecting, RoomStatusText.AutoReconnectDetail);
                _loadTcs?.TrySetException(new EndOfStreamException(RoomStatusText.StreamInterrupted));
                ReconnectRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void StartSeekPolling()
    {
        if (!_seekPollTimer.IsEnabled)
        {
            _seekPollTimer.Start();
        }
    }

    private void StopSeekPolling()
    {
        _seekPollTimer.Stop();
        _seekCommitTimer.Stop();
        _seekLogic.Reset();
        if (SeekBarHost is not null)
        {
            SeekBarHost.IsVisible = false;
        }
    }

    /// <summary>
    /// 每 0.5s 探测一次当前源是否自带回放能力：mpv 报 seekable=yes 且能取到有限 duration
    /// （如支持 Range 的清流直链、HLS 点播/带 DVR 窗口的源）时才显示进度条；
    /// 普通直播流（RTMP/RTSP/FLV/HLS-Live）seekable=no 或不报时长，保持无进度条形态。
    /// 我们不做本地时移缓冲，能回放多少完全取决于源站能力。
    /// </summary>
    private void UpdateSeekBar()
    {
        var context = _context;
        if (context is null || _state != PlayerState.Playing)
        {
            StopSeekPolling();
            return;
        }

        try
        {
            var seekable = context.GetPropertyString("seekable");
            double? duration = string.Equals(seekable, "yes", StringComparison.OrdinalIgnoreCase)
                ? context.GetPropertyDouble("duration")
                : null;
            var position = context.GetPropertyDouble("time-pos");
            ApplySeekBarUpdate(_seekLogic.Update(seekable, duration, position, SeekSlider.Maximum, SeekSlider.Value));
        }
        catch
        {
            // mpv 正在切换/释放时属性读取会失败，等下一个周期。
        }
    }

    private void ApplySeekBarUpdate(SeekBarUpdate update)
    {
        if (update.VisibilityChanged && SeekBarHost is not null)
        {
            SeekBarHost.IsVisible = update.IsVisible;
        }

        if (!update.IsVisible)
        {
            return;
        }

        _suppressSeekChange++;
        try
        {
            if (update.Maximum is { } maximum)
            {
                SeekSlider.Maximum = maximum;
            }

            if (update.Value is { } value)
            {
                SeekSlider.Value = value;
            }
        }
        finally
        {
            _suppressSeekChange--;
        }

        if (update.TimeText is { } text)
        {
            SeekTimeText.Text = text;
        }

        UpdateSeekTip();
    }

    /// <summary>直播与回放两种进度条语义的提示文案，只在真正变化时写一次。</summary>
    private void UpdateSeekTip()
    {
        string tip = _seekLogic.FollowLiveEdge ? LiveSeekTip : ReplaySeekTip;
        if (!string.Equals(Avalonia.Controls.ToolTip.GetTip(SeekSlider) as string, tip, StringComparison.Ordinal))
        {
            Avalonia.Controls.ToolTip.SetTip(SeekSlider, tip);
        }
    }

    private void OnSeekChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeekChange != 0 || !_seekLogic.IsAvailable)
        {
            return;
        }

        _seekLogic.BeginDrag(e.NewValue);
        SeekTimeText.Text = _seekLogic.FormatDragTime(e.NewValue);
        // 拖动会连续触发 ValueChanged：短暂防抖后只发一次 seek，避免一次拖动打出几十条命令。
        _seekCommitTimer.Stop();
        _seekCommitTimer.Start();
    }

    private void CommitSeek()
    {
        _seekCommitTimer.Stop();
        if (!_seekLogic.TryTakePendingTarget(out var target))
        {
            return;
        }

        try
        {
            _context?.Command("seek", target, "absolute+exact");
        }
        catch
        {
            // 流已切换或 mpv 已释放时忽略本次 seek。
        }
    }

    private void StartHealthTimer()
    {
        if (_healthTimer is null)
        {
            _healthTimer = new DispatcherTimer { Interval = HealthInterval };
            _healthTimer.Tick += (_, _) => SampleHealth();
        }

        if (!_healthTimer.IsEnabled)
        {
            _healthTimer.Start();
        }
    }

    private void SampleHealth()
    {
        var context = _context;
        if (context is null)
        {
            return;
        }

        try
        {
            var decoderDrops = context.GetPropertyLong("decoder-frame-drop-count");
            var frameDrops = context.GetPropertyLong("frame-drop-count");
            var containerFps = context.GetPropertyDouble("container-fps");
            var estimatedVfFps = context.GetPropertyDouble("estimated-vf-fps");
            var hwdec = context.GetPropertyString("hwdec-current");

            _metrics = PlayerHealthSampling.Build(
                decoderDrops,
                frameDrops,
                containerFps,
                estimatedVfFps,
                hwdec,
                DateTimeOffset.Now);

            UpdateQualityFps(_metrics.FramesPerSecond);
        }
        catch
        {
            // mpv 正在切换/释放时属性读取会失败，等下一周期。
        }
    }

    /// <summary>用实测源帧率更新底栏档位文案；帧率未知时保持只显示档位名。</summary>
    private void UpdateQualityFps(double fps)
    {
        if (fps <= 0 || string.IsNullOrWhiteSpace(_qualityDisplayName))
        {
            return;
        }

        var label = PlayerQualityText.Format(_qualityDisplayName, fps);
        if (!string.Equals(QualityText.Text, label, StringComparison.Ordinal))
        {
            QualityText.Text = label;
        }
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeChange != 0)
        {
            return;
        }

        SetVolume((int)Math.Round(e.NewValue));
    }

    private void OnMuteClick(object? sender, RoutedEventArgs e)
        => SetMute(!_muted);

    private void OnPauseClick(object? sender, RoutedEventArgs e)
        => TogglePause();

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => RequestRefresh();

    private void OnCompactClick(object? sender, RoutedEventArgs e)
        => SetCompactMode(!_compact);

    private void OnRetryClick(object? sender, RoutedEventArgs e)
        => ReconnectRequested?.Invoke(this, EventArgs.Empty);

    private void SetState(PlayerState state, string? detail)
    {
        _state = state;
        if (state == PlayerState.Playing)
        {
            StartSeekPolling();
        }
        else
        {
            StopSeekPolling();
        }

        ApplyStateVisuals(state, detail);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStateVisuals(PlayerState state, string? detail)
    {
        var overlay = PlayerStateText.Overlay(state);
        StateOverlay.IsVisible = overlay.ShowOverlay;
        LoadingBar.IsVisible = overlay.ShowLoadingBar;
        RetryButton.IsVisible = overlay.ShowRetry;

        StateTitle.Text = overlay.Title;
        StateDetail.Text = detail ?? overlay.Detail;
    }

    /// <summary>
    /// 沉浸模式（桌面全屏 / 焦点模式）下由主窗口控制底部控制条的显隐。
    /// </summary>
    public void SetChromeVisible(bool visible)
    {
        ControlBar.IsVisible = visible;
    }

    /// <summary>
    /// 无弹幕平台（自定义清流）隐藏窗格内的弹幕开关，避免误导；不影响其他控制条元素。
    /// </summary>
    public void SetDanmakuEnabled(bool enabled)
    {
        DanmakuButton.IsVisible = enabled;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MpvPlayerView));
        }
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
