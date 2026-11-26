using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台右侧「导出队列」中的一条任务：显示模式、时间区间、阶段文案、进度与速度，
/// 并负责真正调用 <see cref="IClipExporter"/>。失败/取消都只落在任务自身状态上，
/// 不向界面抛异常，避免一条坏任务卡死整个剪辑台。
/// </summary>
public sealed partial class ClipExportTaskViewModel : ObservableObject
{
    private readonly ClipExportRequest _request;
    private readonly IReadOnlyList<RecordedDanmakuItem>? _danmakuItems;
    private readonly string _sourceVideoCodec;
    private readonly IClipExporter _exporter;

    public ClipExportTaskViewModel(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
        string sourceVideoCodec,
        IClipExporter exporter)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _danmakuItems = danmakuItems;
        _sourceVideoCodec = string.IsNullOrWhiteSpace(sourceVideoCodec) ? "h264" : sourceVideoCodec;
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    public ClipExportRequest Request => _request;

    public string FileName => $"{_request.OutputBaseName}.mp4";

    public string ModeText => _request.Mode switch
    {
        ClipExportMode.CleanOnly => "无弹幕",
        ClipExportMode.BurnedDanmaku => "弹幕烧录",
        _ => "双版"
    };

    public string RangeText => $"{FormatTime(_request.StartTime)} - {FormatTime(_request.EndTime)}";

    [ObservableProperty]
    private string _stateText = "等待导出";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _isCanceled;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _outputText = string.Empty;

    public bool CanCancel => IsRunning;

    public bool HasOutput => IsCompleted && OutputText.Length > 0;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanCancel));

    partial void OnIsCompletedChanged(bool value) => OnPropertyChanged(nameof(HasOutput));

    partial void OnOutputTextChanged(string value) => OnPropertyChanged(nameof(HasOutput));

    public async Task RunAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        IsCompleted = false;
        IsFailed = false;
        IsCanceled = false;
        ErrorMessage = null;
        OutputText = string.Empty;
        Progress = 0;
        SpeedText = string.Empty;
        StateText = "准备导出…";

        var progress = new Progress<ClipExportProgress>(update =>
        {
            Progress = Math.Clamp(update.Percentage, 0, 100);
            StateText = update.StageLabel;
            SpeedText = update.SpeedMultiplier is { } speed ? $"{speed:0.0}x" : string.Empty;
        });

        try
        {
            var files = await _exporter
                .ExportAsync(_request, _danmakuItems, _sourceVideoCodec, progress, ct)
                .ConfigureAwait(true);
            OutputText = string.Join("、", files.Select(Path.GetFileName));
            Progress = 100;
            StateText = "导出完成";
            IsCompleted = true;
        }
        catch (OperationCanceledException)
        {
            IsCanceled = true;
            StateText = "已取消";
            Progress = 0;
        }
        catch (Exception ex)
        {
            IsFailed = true;
            ErrorMessage = ex.Message;
            StateText = "导出失败";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>秒级时间码；超过 1 小时用 h:mm:ss，否则 mm:ss。</summary>
    public static string FormatTime(TimeSpan time) => ClipListDisplayLogic.FormatTime(time);
}
