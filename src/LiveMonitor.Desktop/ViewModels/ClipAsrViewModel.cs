using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台 ASR 子视图模型与父视图模型的边界：读取当前视频/选区、请求 seek、
/// 同步主状态栏并刷新高能卡片的转写按钮。
/// </summary>
internal interface IClipAsrHost
{
    string? CurrentVideoPath { get; }

    TimeSpan Duration { get; }

    TimeSpan InPoint { get; }

    TimeSpan OutPoint { get; }

    bool HasVideo { get; }

    TimeSpan SelectionMinimum { get; }

    void SeekTo(TimeSpan position);

    void SetStatusText(string text);

    void RefreshHighlightAvailability();
}

/// <summary>
/// 剪辑台「语音识别」面板子视图模型：选区/高能片段转写、结果展示、取消与复制提示。
/// 从 <see cref="ClipStudioViewModel"/> 抽离，视频/选区状态经 <see cref="IClipAsrHost"/> 读取；
/// 本类不引用 Avalonia 类型，可独立单测。
/// </summary>
public sealed partial class ClipAsrViewModel : ObservableObject, IDisposable
{
    /// <summary>高能片段转写窗口：以卡片位置为中心，前 10 秒 / 后 20 秒（与主视图模型的跳转选区共用）。</summary>
    internal static readonly TimeSpan HighlightLead = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan HighlightTail = TimeSpan.FromSeconds(20);

    private readonly IAsrProvider _asr;
    private readonly IClipAsrHost _host;

    private int _version;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private AsrTranscriptResult? _displayedResult;

    internal ClipAsrViewModel(IAsrProvider asr, IClipAsrHost host)
    {
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>右侧「语音识别」面板是否显示；当前常显，便于没有结果时也能直接发起选区识别。</summary>
    [ObservableProperty]
    private bool _isPanelVisible = true;

    /// <summary>面板状态文案：「尚未识别」/「正在识别当前选区…」/「识别完成：N 句」/「已取消」等。</summary>
    [ObservableProperty]
    private string _statusText = "尚未识别";

    /// <summary>识别失败的中文消息；成功、取消或清空后为 null。</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>当前是否有转写任务在跑（全局互斥：同一时刻只允许一个）。</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>结果来源标签，如「groq (whisper-large-v3-turbo)」。</summary>
    [ObservableProperty]
    private string _providerLabel = string.Empty;

    /// <summary>最近一次识别结果的整段文本，供「复制文本」使用。</summary>
    [ObservableProperty]
    private string _fullText = string.Empty;

    /// <summary>最近一次展示的识别片段范围，如「00:10 - 00:40」。</summary>
    [ObservableProperty]
    private string _rangeText = string.Empty;

    /// <summary>分段结果（相对片段起点的时间戳），点击单句可跳转预览位置。</summary>
    public ObservableCollection<AsrTranscriptLineViewModel> TranscriptLines { get; } = new();

    public bool HasResult => _displayedResult is not null;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>当前选区是否可发起识别：已加载视频、没有进行中的转写、选区时长有效。</summary>
    public bool CanTranscribeSelection =>
        _host.HasVideo && !IsRunning && _host.OutPoint - _host.InPoint >= _host.SelectionMinimum;

    /// <summary>识别当前时间轴选区（InPoint → OutPoint）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTranscribeSelection))]
    private async Task TranscribeSelectionAsync()
    {
        var videoPath = _host.CurrentVideoPath;
        if (videoPath is null || _host.Duration <= TimeSpan.Zero)
        {
            ReportUnavailable();
            return;
        }

        var start = _host.InPoint;
        var end = _host.OutPoint;
        if (end - start < _host.SelectionMinimum)
        {
            ErrorText = AsrStatusText.SelectionInvalidError;
            StatusText = AsrStatusText.SelectionInvalid;
            return;
        }

        await RunAsrAsync(videoPath, start, end, highlight: null).ConfigureAwait(true);
    }

    /// <summary>转写某条高能推荐卡片的 [位置-10s, 位置+20s] 区间。</summary>
    [RelayCommand]
    private async Task TranscribeHighlightAsync(ClipHighlightItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var videoPath = _host.CurrentVideoPath;
        var duration = _host.Duration;
        if (videoPath is null || duration <= TimeSpan.Zero)
        {
            ReportUnavailable();
            return;
        }

        if (item.AsrResult is { } existing)
        {
            // 已有结果（含 Provider 缓存命中）时只把同一结果展开到右侧面板，不重复发起任务。
            ShowResult(existing, existing.ClipStart, existing.ClipEnd);
            StatusText = BuildCompletedText();
            return;
        }

        if (IsRunning)
        {
            return;
        }

        var start = Clamp(item.Position - HighlightLead, TimeSpan.Zero, duration);
        var end = Clamp(item.Position + HighlightTail, TimeSpan.Zero, duration);
        if (end - start < _host.SelectionMinimum)
        {
            item.AsrError = AsrStatusText.HighlightTooShortError;
            ErrorText = item.AsrError;
            StatusText = AsrStatusText.HighlightTooShort;
            return;
        }

        await RunAsrAsync(videoPath, start, end, item).ConfigureAwait(true);
    }

    /// <summary>点击卡片摘要：把该卡片已有结果展开到右侧面板（不强制 seek）。</summary>
    [RelayCommand]
    private void ShowHighlightAsr(ClipHighlightItemViewModel? item)
    {
        if (item?.AsrResult is not { } result)
        {
            return;
        }

        ShowResult(result, result.ClipStart, result.ClipEnd);
        StatusText = BuildCompletedText();
        IsPanelVisible = true;
    }

    /// <summary>取消进行中的转写；状态会在任务收尾时变为「已取消」。</summary>
    [RelayCommand]
    private void CancelAsr()
    {
        if (!IsRunning || _cts is null)
        {
            return;
        }

        StatusText = AsrStatusText.Canceling;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>右侧面板点击单句：把播放器 seek 到「结果片段起点 + 相对偏移」的绝对时间。</summary>
    public void SeekToLine(AsrTranscriptLineViewModel? line)
    {
        if (line is null || _displayedResult is null || _host.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _host.SeekTo(_displayedResult.ClipStart + line.RelativeStart);
    }

    /// <summary>复制成功后由 View 调用：面板状态改为「已复制到剪贴板」。</summary>
    public void NotifyTextCopied()
    {
        ErrorText = null;
        StatusText = AsrStatusText.Copied;
    }

    /// <summary>复制失败时由 View 调用：把异常转成面板上的中文提示，避免 async void 事件处理器崩溃。</summary>
    public void NotifyCopyFailed(string message)
    {
        ErrorText = AsrStatusText.CopyFailed(message);
    }

    /// <summary>父视图模型的视频/选区/时长变化后调用：刷新命令可用性与绑定。</summary>
    public void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanTranscribeSelection));
        TranscribeSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 切换视频/场次时清空面板并取消进行中的转写。
    /// 高能卡片上的识别状态由父视图模型在调用本方法后同步清理（见 <c>ResetHighlightAsrState</c>）。
    /// </summary>
    public void Reset()
    {
        _version++;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _displayedResult = null;
        TranscriptLines.Clear();
        FullText = string.Empty;
        ProviderLabel = string.Empty;
        RangeText = string.Empty;
        ErrorText = null;
        StatusText = AsrStatusText.Idle;
        OnPropertyChanged(nameof(HasResult));
        NotifyAvailabilityChanged();
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
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();
        _cts = null;
    }

    private bool CanRunTranscribeSelection() => CanTranscribeSelection;

    /// <summary>
    /// 统一的转写执行入口：全局互斥、取消与异常都归一为面板文案，不向 UI 线程抛。
    /// 成功后同时回填右侧面板与（高能入口的）卡片结果。
    /// </summary>
    private async Task RunAsrAsync(
        string videoPath,
        TimeSpan start,
        TimeSpan end,
        ClipHighlightItemViewModel? highlight)
    {
        if (IsRunning)
        {
            return;
        }

        var version = ++_version;
        ReplaceCancellationSource();
        var ct = _cts!.Token;

        IsRunning = true;
        ErrorText = null;
        StatusText = highlight is null ? AsrStatusText.RunningSelection : AsrStatusText.RunningHighlight;
        if (highlight is not null)
        {
            highlight.IsTranscribing = true;
            highlight.AsrError = null;
        }

        try
        {
            var result = await _asr.TranscribeSegmentAsync(videoPath, start, end, ct).ConfigureAwait(true);
            if (version != _version || _disposed)
            {
                return;
            }

            if (highlight is not null)
            {
                highlight.AsrResult = result;
            }

            ShowResult(result, start, end);
            StatusText = BuildCompletedText();
        }
        catch (OperationCanceledException)
        {
            if (version == _version && !_disposed)
            {
                ErrorText = null;
                StatusText = AsrStatusText.Canceled;
            }
        }
        catch (Exception ex)
        {
            if (version == _version && !_disposed)
            {
                var message = string.IsNullOrWhiteSpace(ex.Message)
                    ? "语音识别失败，请稍后重试。"
                    : ex.Message.Trim();
                ErrorText = message;
                StatusText = AsrStatusText.Failed;
                _host.SetStatusText($"语音识别失败：{message}");
                if (highlight is not null)
                {
                    highlight.AsrError = message;
                }
            }
        }
        finally
        {
            if (highlight is not null)
            {
                highlight.IsTranscribing = false;
            }

            // 全局互斥标记必须无条件复位：即使期间切换了视频（version 变化）也不能卡在「转写中」。
            IsRunning = false;
        }
    }

    /// <summary>把一次识别结果写入右侧面板：全文、分段行、片段范围与来源标签。</summary>
    private void ShowResult(AsrTranscriptResult result, TimeSpan start, TimeSpan end)
    {
        _displayedResult = result;
        ProviderLabel = string.IsNullOrWhiteSpace(result.ProviderLabel) ? "未知服务商" : result.ProviderLabel.Trim();
        FullText = (result.FullText ?? string.Empty).Trim();
        RangeText = $"{ClipExportTaskViewModel.FormatTime(start)} - {ClipExportTaskViewModel.FormatTime(end)}";
        TranscriptLines.Clear();

        if (result.Segments is { Count: > 0 } segments)
        {
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                TranscriptLines.Add(new AsrTranscriptLineViewModel(segment));
            }
        }

        if (TranscriptLines.Count == 0 && FullText.Length > 0)
        {
            // 纯文本响应（没有分段时间戳）也照常显示为一行，点击跳转到片段起点。
            TranscriptLines.Add(new AsrTranscriptLineViewModel(
                new AsrTranscriptSegment(TimeSpan.Zero, end - start, FullText)));
        }

        if (FullText.Length == 0 && TranscriptLines.Count > 0)
        {
            FullText = string.Join(Environment.NewLine, TranscriptLines.Select(line => line.Text));
        }

        OnPropertyChanged(nameof(HasResult));
    }

    private string BuildCompletedText() =>
        TranscriptLines.Count > 0 ? $"识别完成：{TranscriptLines.Count} 句" : "识别完成：未识别到文本";

    /// <summary>没有视频时的友好提示：同时写入状态与错误行，保证面板可见。</summary>
    private void ReportUnavailable()
    {
        ErrorText = AsrStatusText.NoVideo;
        StatusText = AsrStatusText.NoVideo;
    }

    private void ReplaceCancellationSource()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts = new CancellationTokenSource();
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    partial void OnIsRunningChanged(bool value)
    {
        NotifyAvailabilityChanged();
        _host.RefreshHighlightAvailability();
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
