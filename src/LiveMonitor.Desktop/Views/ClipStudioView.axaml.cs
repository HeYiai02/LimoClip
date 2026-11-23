using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

/// <summary>
/// V0.4 剪辑台视图：把 <see cref="ClipStudioViewModel"/> 的请求桥接到本地播放器与
/// 时间轴控件，并把播放器/时间轴事件回灌给 ViewModel。除事件桥接外不持有业务状态。
/// </summary>
public partial class ClipStudioView : Avalonia.Controls.UserControl
{
    private ClipStudioViewModel? _viewModel;
    private bool _syncingTimeline;

    public ClipStudioView()
    {
        InitializeComponent();

        Timeline.SeekRequested += OnTimelineSeekRequested;
        Timeline.InOutChanged += OnTimelineInOutChanged;

        Player.PositionChanged += OnPlayerPositionChanged;
        Player.StateChanged += OnPlayerStateChanged;
        Player.PlaybackCompleted += OnPlayerCompleted;
        Player.PlaybackFailed += OnPlayerFailed;

        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    /// <summary>窗口关闭前调用：暂停并释放 libmpv 上下文。</summary>
    public void Shutdown()
    {
        _viewModel?.SuspendPlayback();
        Player.Dispose();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            Detach(_viewModel);
        }

        _viewModel = DataContext as ClipStudioViewModel;
        if (_viewModel is not null)
        {
            Attach(_viewModel);
        }

        ApplyTimeline();
    }

    private void Attach(ClipStudioViewModel viewModel)
    {
        viewModel.TimelineDataChanged += OnTimelineDataChanged;
        viewModel.LoadVideoRequested += OnLoadVideoRequested;
        viewModel.PlayPauseRequested += OnPlayPauseRequested;
        viewModel.PauseRequested += OnPauseRequested;
        viewModel.SeekRequested += OnSeekRequested;
        viewModel.SpeedChangeRequested += OnSpeedChangeRequested;
        viewModel.DanmakuEnabledChanged += OnDanmakuEnabledChanged;
        viewModel.DanmakuSyncRequested += OnDanmakuSyncRequested;
    }

    private void Detach(ClipStudioViewModel viewModel)
    {
        viewModel.TimelineDataChanged -= OnTimelineDataChanged;
        viewModel.LoadVideoRequested -= OnLoadVideoRequested;
        viewModel.PlayPauseRequested -= OnPlayPauseRequested;
        viewModel.PauseRequested -= OnPauseRequested;
        viewModel.SeekRequested -= OnSeekRequested;
        viewModel.SpeedChangeRequested -= OnSpeedChangeRequested;
        viewModel.DanmakuEnabledChanged -= OnDanmakuEnabledChanged;
        viewModel.DanmakuSyncRequested -= OnDanmakuSyncRequested;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // 切换到其他视图时静默暂停，避免后台继续解码/出声；播放器上下文保留以便快速返回。
        _viewModel?.SuspendPlayback();
    }

    private async void OnLoadVideoRequested(string filePath)
    {
        try
        {
            await Player.LoadAsync(filePath);
            Player.SetDanmakuEnabled(_viewModel?.DanmakuPlaybackEnabled ?? true);
            Player.SetSpeed(_viewModel?.Speed ?? 1.0);
        }
        catch (Exception ex)
        {
            _viewModel?.OnPlayerFailed(ex.Message);
        }
    }

    private void OnPlayPauseRequested() => Player.TogglePlayPause();

    private void OnPauseRequested() => Player.Pause();

    private void OnSeekRequested(TimeSpan position) => Player.Seek(position);

    private void OnSpeedChangeRequested(double speed) => Player.SetSpeed(speed);

    private void OnDanmakuEnabledChanged(bool enabled)
    {
        Player.SetDanmakuEnabled(enabled);
        if (!enabled)
        {
            Player.ClearDanmaku();
        }
    }

    private void OnDanmakuSyncRequested(IReadOnlyList<DanmakuMessage> messages, bool clearFirst)
    {
        if (clearFirst)
        {
            Player.ClearDanmaku();
        }

        Player.PushDanmaku(messages);
    }

    private void OnTimelineDataChanged(object? sender, EventArgs e) => ApplyTimeline();

    private void OnTimelineSeekRequested(TimeSpan position) => _viewModel?.SeekTo(position);

    private void OnTimelineInOutChanged(object? sender, EventArgs e)
    {
        if (_syncingTimeline || _viewModel is null)
        {
            return;
        }

        _viewModel.UpdateSelection(Timeline.InPoint, Timeline.OutPoint);
    }

    private void OnPlayerPositionChanged(object? sender, EventArgs e)
    {
        Timeline.Position = Player.TimePos;
        _viewModel?.OnPlayerPositionChanged(Player.TimePos);
    }

    private void OnPlayerStateChanged(object? sender, EventArgs e)
    {
        _viewModel?.OnPlayerStateChanged(Player.IsPlaying);
    }

    private void OnPlayerCompleted(object? sender, EventArgs e) => _viewModel?.OnPlayerCompleted();

    private void OnPlayerFailed(string message) => _viewModel?.OnPlayerFailed(message);

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => _viewModel?.PlayPause();

    private void OnSeekBackClick(object? sender, RoutedEventArgs e) => _viewModel?.SeekBy(TimeSpan.FromSeconds(-5));

    private void OnSeekForwardClick(object? sender, RoutedEventArgs e) => _viewModel?.SeekBy(TimeSpan.FromSeconds(5));

    private void OnSetInClick(object? sender, RoutedEventArgs e) => _viewModel?.SetInFromPosition();

    private void OnSetOutClick(object? sender, RoutedEventArgs e) => _viewModel?.SetOutFromPosition();

    private void OnHighlightClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button { DataContext: ClipHighlightItemViewModel item })
        {
            _viewModel?.JumpToHighlightCommand.Execute(item);
        }
    }

    /// <summary>高能卡片「🎙 转写」：把卡片上对应的高能片段送云端识别。</summary>
    private void OnTranscribeHighlightClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is ClipHighlightItemViewModel item)
        {
            // DataContext 已切换到其它 ViewModel 时 _viewModel 可能为 null，静默忽略。
            _viewModel?.TranscribeHighlightCommand.Execute(item);
        }
    }

    /// <summary>点击卡片上的识别摘要：在右侧面板展开该卡片已有的转写结果（不强制 seek）。</summary>
    private void OnHighlightAsrSummaryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is ClipHighlightItemViewModel item)
        {
            _viewModel?.ShowHighlightAsrCommand.Execute(item);
        }
    }

    /// <summary>右侧识别面板点击单句：seek 到「片段起点 + 相对偏移」的绝对时间。</summary>
    private void OnAsrLineClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is AsrTranscriptLineViewModel line)
        {
            _viewModel?.SeekToAsrLine(line);
        }
    }

    /// <summary>复制整段识别文本（剪贴板只在 View 层访问，ViewModel 不引用 Avalonia 类型）。</summary>
    private async void OnCopyAsrTextClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var text = _viewModel.AsrFullText;
        if (string.IsNullOrWhiteSpace(text))
        {
            _viewModel.NotifyAsrCopyFailed("没有可复制的识别文本。");
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                _viewModel.NotifyAsrCopyFailed("当前窗口不支持剪贴板。");
                return;
            }

            await clipboard.SetTextAsync(text);
            _viewModel.NotifyAsrTextCopied();
        }
        catch (Exception ex)
        {
            // async void 事件处理器不能把异常抛回 UI 线程，统一转成面板提示。
            _viewModel.NotifyAsrCopyFailed(ex.Message);
        }
    }

    /// <summary>把 ViewModel 的时间轴数据写入控件；期间抑制 InOutChanged 回传。</summary>
    private void ApplyTimeline()
    {
        if (_viewModel is null)
        {
            return;
        }

        _syncingTimeline = true;
        try
        {
            Timeline.Duration = _viewModel.Duration;
            Timeline.Waveform = _viewModel.Waveform;
            Timeline.InPoint = _viewModel.InPoint;
            Timeline.OutPoint = _viewModel.OutPoint;
            Timeline.Position = _viewModel.Position;
            Timeline.Highlights = _viewModel.HighlightMarkers;
        }
        finally
        {
            _syncingTimeline = false;
        }
    }
}
