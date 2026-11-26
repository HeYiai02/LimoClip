using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibMpv.Client;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Player;

/// <summary>本地剪辑预览的播放状态。</summary>
public enum ClipPlayerState
{
    /// <summary>还没有打开任何视频。</summary>
    Empty,

    /// <summary>正在加载视频。</summary>
    Loading,

    /// <summary>已加载并在首帧暂停。</summary>
    Ready,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>用户暂停。</summary>
    Paused,

    /// <summary>播放到末尾（keep-open 保持最后一帧）。</summary>
    Ended,

    /// <summary>加载/解码失败。</summary>
    Failed,
}

/// <summary>
/// 剪辑台本地预览播放器：一个独立的 mpv context（hr-seek / keep-open / 32M 回退缓存），
/// 与直播播放器的配置、流地址、重连逻辑完全隔离，避免把直播参数带进本地文件。
/// <para>
/// 只负责播放与时间轴，不关心导出与弹幕调度：时间位置通过 100ms 轮询 + 事件暴露，
/// 弹幕回放由上层用 <c>DanmakuPlaybackCursor</c> 推进后调用 <see cref="PushDanmaku"/>。
/// </para>
/// </summary>
public partial class LocalClipPlayerView : Avalonia.Controls.UserControl, IDisposable
{
    private static readonly TimeSpan RenderReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly DispatcherTimer _positionTimer;
    private MpvContext? _context;
    private TaskCompletionSource<bool>? _loadTcs;
    private Task? _disposeTask;
    private int _disposed;
    private string? _filePath;
    private TimeSpan _duration;
    private TimeSpan _position;
    private double _speed = 1.0;
    private ClipPlayerState _state = ClipPlayerState.Empty;
    private bool _paused = true;
    private bool _completionRaised;
    private bool _isLive;
    private TimeSpan _liveSafeDuration;

    public LocalClipPlayerView()
    {
        InitializeComponent();
        _positionTimer = new DispatcherTimer { Interval = PositionPollInterval };
        _positionTimer.Tick += (_, _) => PollPosition();
        ApplyStateVisuals(ClipPlayerState.Empty, null);
    }

    /// <summary>播放状态变化。</summary>
    public event EventHandler? StateChanged;

    /// <summary>播放位置变化（100ms 轮询 + seek 立即触发）。</summary>
    public event EventHandler? PositionChanged;

    /// <summary>时长可用/更新。</summary>
    public event EventHandler? DurationChanged;

    /// <summary>自然播放到末尾。</summary>
    public event EventHandler? PlaybackCompleted;

    /// <summary>加载/播放失败，参数为面向用户的错误信息。</summary>
    public event Action<string>? PlaybackFailed;

    public ClipPlayerState State => _state;

    public new bool IsLoaded => _filePath is not null && ClipPlaybackStateLogic.IsLoadedState(_state);

    public bool IsPlaying => _state == ClipPlayerState.Playing;

    public string? FilePath => _filePath;

    public TimeSpan TimePos => _position;

    public TimeSpan Duration => _duration;

    public double Speed => _speed;

    /// <summary>当前是否以“边录边剪”模式加载（分片仍在写入）。</summary>
    public bool IsLive => _isLive;

    /// <summary>边录边剪的安全可剪截止时间（Seek 上界）。</summary>
    public TimeSpan SafeDuration => _liveSafeDuration;

    /// <summary>
    /// 刷新边录边剪的安全可剪截止时间：同步后安全区会前移，调用方无需重载文件即可放宽 seek 上界。
    /// 非 live 模式下忽略。
    /// </summary>
    public void UpdateLiveSafeDuration(TimeSpan safeDuration)
    {
        ThrowIfDisposed();
        if (!_isLive)
        {
            return;
        }

        _liveSafeDuration = ClipPlaybackPositionLogic.ClampSafeDuration(safeDuration);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            EnsureContext();
        }
        catch
        {
            // 真正的错误在 LoadAsync 里呈现。
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

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
            // 旧渲染上下文可能已随窗口销毁。
        }
    }

    /// <summary>加载本地视频并停在首帧；失败抛出异常并切到 Failed 状态。</summary>
    public async Task LoadAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ThrowIfDisposed();

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadAsync(filePath, ct));
            return;
        }

        // 普通离线文件：退出 live 模式的 seek 上界限制。
        _isLive = false;
        _liveSafeDuration = TimeSpan.Zero;
        await LoadAsyncCore(filePath, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 以“边录边剪”方式加载正在写入的分片：先强制 mpv 把文件当可 seek，
    /// 其余流程复用 <see cref="LoadAsync"/>。seek 上界由 <paramref name="safeDuration"/> 决定。
    /// </summary>
    public async Task LoadLiveRecordingAsync(string filePath, TimeSpan safeDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ThrowIfDisposed();

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadLiveRecordingAsync(filePath, safeDuration, ct));
            return;
        }

        _isLive = true;
        _liveSafeDuration = safeDuration < TimeSpan.Zero ? TimeSpan.Zero : safeDuration;
        try
        {
            var context = EnsureContext();
            // 录制中的 mkv 仍在前端追加 cluster：强制按可 seek 处理，避免 seek 被直接拒绝。
            try
            {
                context.SetOptionString("force-seekable", "yes");
            }
            catch
            {
                try
                {
                    context.SetPropertyFlag("force-seekable", true);
                }
                catch
                {
                    // 运行期不支持该选项时忽略：仍可用重载 + 轮询位置兜底。
                }
            }

            await LoadAsyncCore(filePath, ct).ConfigureAwait(true);
        }
        catch
        {
            _isLive = false;
            _liveSafeDuration = TimeSpan.Zero;
            throw;
        }
    }

    /// <summary>
    /// 边录边剪同步后无感重载分片：保持暂停状态与倍速，并跳到 <paramref name="position"/>。
    /// 先 loadfile 再精确 seek，避免同步瞬间闪回首帧。
    /// </summary>
    public async Task ReloadAtPositionAsync(TimeSpan position, TimeSpan? safeDuration = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ReloadAtPositionAsync(position, safeDuration, ct));
            return;
        }

        // 同步后安全区前移：重载前先刷新上界，否则 ClampPosition 仍按旧的 safe 限制。
        if (_isLive && safeDuration is { } safe)
        {
            _liveSafeDuration = safe < TimeSpan.Zero ? TimeSpan.Zero : safe;
        }

        var filePath = _filePath;
        if (filePath is null)
        {
            return;
        }

        var previousState = _state;
        var wasPlaying = previousState == ClipPlayerState.Playing;
        var target = ClampPosition(position);

        var loadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadTcs = loadTcs;
        try
        {
            var context = EnsureContext();
            context.SetPropertyFlag("pause", true);
            context.Command("loadfile", filePath, "replace");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoadTimeout);
            using var registration = timeout.Token.Register(
                () => loadTcs.TrySetException(new TimeoutException("边录边剪同步重载超时")));

            await loadTcs.Task.ConfigureAwait(true);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            ApplySpeed();
            _duration = ReadDuration();
            _position = target;
            _completionRaised = false;

            // 先精确 seek 到原位置，再恢复暂停/播放状态与弹幕层。
            try
            {
                context.Command(
                    "seek",
                    target.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    "absolute+exact");
            }
            catch
            {
                // mpv 释放中忽略。
            }

            if (wasPlaying)
            {
                Play();
            }
            else
            {
                _paused = true;
                SetState(previousState == ClipPlayerState.Ended ? ClipPlayerState.Ended : ClipPlayerState.Paused, null);
            }

            DurationChanged?.Invoke(this, EventArgs.Empty);
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetState(ClipPlayerState.Failed, ex.Message);
            PlaybackFailed?.Invoke(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadTcs, loadTcs))
            {
                _loadTcs = null;
            }
        }
    }

    private async Task LoadAsyncCore(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            var missing = ClipPlaybackStateLogic.BuildMissingFileMessage(filePath);
            SetState(ClipPlayerState.Failed, missing);
            PlaybackFailed?.Invoke(missing);
            throw new FileNotFoundException(missing, filePath);
        }

        _filePath = filePath;
        _duration = TimeSpan.Zero;
        _position = TimeSpan.Zero;
        _paused = true;
        _completionRaised = false;
        SetState(ClipPlayerState.Loading, ClipPlaybackStateLogic.LoadingDetail);

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

            // 起播即暂停，加载后停在首帧，等用户在剪辑台点播放。
            context.SetPropertyFlag("pause", true);
            context.Command("loadfile", filePath, "replace");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoadTimeout);
            using var registration = timeout.Token.Register(
                () => loadTcs.TrySetException(new TimeoutException("本地视频加载超时")));

            await loadTcs.Task.ConfigureAwait(true);

            ApplySpeed();
            _duration = ReadDuration();
            _position = TimeSpan.Zero;
            _paused = true;
            _positionTimer.Start();
            SetState(ClipPlayerState.Ready, null);
            DurationChanged?.Invoke(this, EventArgs.Empty);
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(ClipPlayerState.Empty, ClipPlaybackStateLogic.CancelledDetail);
            throw;
        }
        catch (Exception ex)
        {
            SetState(ClipPlayerState.Failed, ex.Message);
            PlaybackFailed?.Invoke(ex.Message);
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

    /// <summary>开始/继续播放；播放完成后再次调用会回到开头重播。</summary>
    public void Play()
    {
        ThrowIfDisposed();
        if (!IsLoaded)
        {
            return;
        }

        if (ClipPlaybackStateLogic.ShouldRestartBeforePlay(_state))
        {
            Seek(TimeSpan.Zero);
        }

        _completionRaised = false;
        _paused = false;
        try
        {
            _context?.SetPropertyFlag("pause", false);
        }
        catch
        {
            // mpv 释放中忽略。
        }

        SetState(ClipPlayerState.Playing, null);

        // 暂停期间弹幕层也要同步解冻。
        DanmakuLayer.IsPaused = false;
    }

    /// <summary>暂停。</summary>
    public void Pause()
    {
        if (!ClipPlaybackStateLogic.CanPause(_state))
        {
            return;
        }

        _paused = true;
        try
        {
            _context?.SetPropertyFlag("pause", true);
        }
        catch
        {
            // mpv 释放中忽略。
        }

        SetState(ClipPlayerState.Paused, null);
        DanmakuLayer.IsPaused = true;
    }

    /// <summary>切换播放/暂停。</summary>
    public void TogglePlayPause()
    {
        if (_state == ClipPlayerState.Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>绝对跳转（hr-seek）。</summary>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded)
        {
            return;
        }

        var target = ClampPosition(position);
        try
        {
            _context?.Command(
                "seek",
                target.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                "absolute+exact");
        }
        catch
        {
            // mpv 释放中忽略。
        }

        _position = target;
        _completionRaised = false;
        if (ClipPlaybackStateLogic.ShouldReturnToPausedAfterSeek(_state))
        {
            SetState(ClipPlayerState.Paused, null);
        }

        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>相对跳转（±1s / ±5s）。</summary>
    public void SeekBy(TimeSpan delta) => Seek(_position + delta);

    /// <summary>设置播放倍速（0.5x~2.0x），同时同步弹幕层速度。</summary>
    public void SetSpeed(double speed)
    {
        _speed = ClipPlaybackStateLogic.ClampSpeed(speed);
        ApplySpeed();
        DanmakuLayer.PlaybackRate = _speed;
    }

    /// <summary>打开/关闭弹幕回放渲染。</summary>
    public void SetDanmakuEnabled(bool enabled)
    {
        DanmakuLayer.IsVisible = enabled;
        if (!enabled)
        {
            DanmakuLayer.Clear();
        }
    }

    /// <summary>把一批弹幕加入渲染队列。</summary>
    public void PushDanmaku(IReadOnlyList<DanmakuMessage> messages)
    {
        if (messages.Count > 0)
        {
            DanmakuLayer.EnqueueRange(messages);
        }
    }

    /// <summary>清空屏幕上的回放弹幕（seek 时用）。</summary>
    public void ClearDanmaku() => DanmakuLayer.Clear();

    /// <summary>暂停/恢复弹幕推进（与视频暂停同步）。</summary>
    public void SetDanmakuPaused(bool paused) => DanmakuLayer.IsPaused = paused;

    /// <summary>设置弹幕移动倍率（与播放倍速同步）。</summary>
    public void SetDanmakuPlaybackRate(double rate) => DanmakuLayer.PlaybackRate = rate;

    private void ApplySpeed()
    {
        try
        {
            _context?.SetPropertyString("speed", _speed.ToString("0.00", CultureInfo.InvariantCulture));
        }
        catch
        {
            // mpv 释放中忽略。
        }
    }

    private MpvContext EnsureContext()
    {
        lock (_gate)
        {
            if (_context is not null)
            {
                if (!ReferenceEquals(Video.MpvContext, _context))
                {
                    Video.MpvContext = _context;
                }

                return _context;
            }

            // 随包 libmpv 是 version=2（libmpv-2.dll），与直播播放器一致。
            LibMpv.Client.LibMpv.UseLibMpv(2);
            var context = new MpvContext();

            // 渲染与硬件解码：沿用直播端已验证的组合。
            context.SetOptionString("hwdec", "auto-safe");
            context.SetOptionString("vo", "libmpv");
            context.SetOptionString("gpu-hwdec-interop", "auto");
            context.SetOptionString("gpu-api", "opengl");

            // 本地剪辑专用：精确 seek + 到末尾保留最后一帧 + 更宽的 demuxer 回退缓存。
            context.SetOptionString("cache", "yes");
            context.SetOptionString("demuxer-max-bytes", "64M");
            context.SetOptionString("demuxer-max-back-bytes", "32M");
            context.SetOptionString("hr-seek", "yes");
            context.SetOptionString("hr-seek-framedrop", "no");
            context.SetOptionString("keep-open", "yes");
            context.SetOptionString("pause", "yes");

            // 本地预览必须出声；音量由剪辑台统一控制，不受直播全局静音影响。
            context.SetOptionString("aid", "auto");
            context.SetOptionString("mute", "no");
            context.SetOptionString("volume", "100");
            context.SetOptionString("audio-exclusive", "no");
            context.SetOptionString("audio-stream-silence", "yes");

            context.SetOptionString("osc", "no");
            context.SetOptionString("osd-level", "0");
            context.SetOptionString("input-default-bindings", "no");

            context.StartFile += OnStartFile;
            context.FileLoaded += OnFileLoaded;
            context.EndFile += OnEndFile;

            _context = context;
            Video.MpvContext = context;
            ApplySpeed();
            return context;
        }
    }

    private void PollPosition()
    {
        var context = _context;
        if (context is null || !IsLoaded)
        {
            return;
        }

        try
        {
            var seconds = context.GetPropertyDouble("time-pos");
            if (seconds is null)
            {
                return;
            }

            var sampled = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
            if (!_isLive && _duration <= TimeSpan.Zero)
            {
                _duration = ReadDuration();
                if (_duration > TimeSpan.Zero)
                {
                    DurationChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            // 边录边剪：分片仍在前端增长，mpv 的 EOF 只代表“当前已写入尾部”，不能当作播放完成；
            // 同时把播放位置钳在安全可剪范围内，避免播放头/界面位置进入尚未安全写入的尾部。
            var result = ClipPlaybackPositionLogic.Evaluate(
                sampled,
                _position,
                _isLive,
                _liveSafeDuration,
                _duration,
                _paused);
            if (result.PositionChanged)
            {
                _position = result.Position;
                PositionChanged?.Invoke(this, EventArgs.Empty);
            }

            if (result.Completed)
            {
                RaiseCompleted();
            }
        }
        catch
        {
            // mpv 正在切换/释放时属性读取会失败，等下一周期。
        }
    }

    private TimeSpan ReadDuration()
    {
        try
        {
            var seconds = _context?.GetPropertyDouble("duration");
            if (seconds is { } value && value > 0 && double.IsFinite(value))
            {
                return TimeSpan.FromSeconds(value);
            }
        }
        catch
        {
            // 属性暂不可读。
        }

        return TimeSpan.Zero;
    }

    private TimeSpan ClampPosition(TimeSpan position) =>
        ClipPlaybackPositionLogic.ClampPosition(position, _isLive, _liveSafeDuration, _duration);

    private void OnStartFile(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed == 0 && ClipPlaybackStateLogic.ShouldShowPreparingDetail(_state))
            {
                SetState(ClipPlayerState.Loading, ClipPlaybackStateLogic.PreparingDetail);
            }
        });
    }

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed != 0)
            {
                return;
            }

            _duration = ReadDuration();
            _loadTcs?.TrySetResult(true);
        });
    }

    private void OnEndFile(object? sender, MpvEndFileEventArgs e)
    {
        var reason = e.EventData.Reason switch
        {
            MpvEndFileReason.MpvEndFileReasonStop => ClipEndReason.Stop,
            MpvEndFileReason.MpvEndFileReasonQuit => ClipEndReason.Quit,
            MpvEndFileReason.MpvEndFileReasonError => ClipEndReason.Error,
            MpvEndFileReason.MpvEndFileReasonEof => ClipEndReason.Eof,
            _ => ClipEndReason.Other
        };
        var error = e.EventData.Error;

        if (reason is ClipEndReason.Stop or ClipEndReason.Quit)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed != 0)
            {
                return;
            }

            var isLoading = _state == ClipPlayerState.Loading;
            var action = ClipPlaybackStateLogic.ClassifyEnd(reason, IsLoaded, isLoading);
            if (action == ClipEndAction.FailWhileLoading)
            {
                _loadTcs?.TrySetException(
                    new InvalidOperationException(ClipPlaybackStateLogic.BuildPlaybackFailedMessage(error)));
                return;
            }

            if (action == ClipEndAction.Fail)
            {
                if (!IsLoaded && _state != ClipPlayerState.Failed)
                {
                    return;
                }

                var message = ClipPlaybackStateLogic.BuildPlaybackFailedMessage(error);
                SetState(ClipPlayerState.Failed, message);
                PlaybackFailed?.Invoke(message);
                return;
            }

            if (action == ClipEndAction.Complete)
            {
                RaiseCompleted();
            }
        });
    }

    private void RaiseCompleted()
    {
        if (!ClipPlaybackStateLogic.CanRaiseCompletion(_state, _completionRaised))
        {
            return;
        }

        _completionRaised = true;
        _paused = true;
        try
        {
            // keep-open 下 mpv 停在最后一帧，这里保持一致性。
            _context?.SetPropertyFlag("pause", true);
        }
        catch
        {
            // mpv 释放中忽略。
        }

        _position = ClipPlaybackStateLogic.CompletionPosition(_duration, _position);
        DanmakuLayer.IsPaused = true;
        SetState(ClipPlayerState.Ended, null);
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(ClipPlayerState state, string? detail)
    {
        if (!ClipPlaybackStateLogic.ShouldRaiseStateChanged(_state, state))
        {
            return;
        }

        _state = state;
        ApplyStateVisuals(state, detail);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStateVisuals(ClipPlayerState state, string? detail)
    {
        var showOverlay = state is ClipPlayerState.Empty or ClipPlayerState.Loading or ClipPlayerState.Failed;
        StateOverlay.IsVisible = showOverlay;
        LoadingBar.IsVisible = state == ClipPlayerState.Loading;
        StateOverlay.IsHitTestVisible = false;

        StateTitle.Text = state switch
        {
            ClipPlayerState.Empty => "本地剪辑预览",
            ClipPlayerState.Loading => "加载中",
            ClipPlayerState.Failed => "播放失败",
            _ => string.Empty,
        };

        StateDetail.Text = detail ?? state switch
        {
            ClipPlayerState.Empty => "从左侧列表选择录像，或打开本地文件",
            ClipPlayerState.Loading => "正在读取视频…",
            ClipPlayerState.Failed => "请检查文件是否完整",
            _ => string.Empty,
        };
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LocalClipPlayerView));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _positionTimer.Stop();
        _loadTcs?.TrySetCanceled();
        DanmakuLayer.Clear();

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
                    // 已结束的文件可能报错，忽略。
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
    }
}
