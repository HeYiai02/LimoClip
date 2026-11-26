using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台导出子视图模型与父视图模型的边界：读取当前视频/选区/边录边剪状态与编码器信息，
/// 写回安全区钳制后的选区与全局状态栏，并拿到导出器依赖。
/// </summary>
internal interface IClipExportHost
{
    string? CurrentVideoPath { get; }

    bool HasVideo { get; }

    TimeSpan InPoint { get; }

    TimeSpan OutPoint { get; }

    TimeSpan SelectionMinimum { get; }

    bool IsLiveSession { get; }

    TimeSpan SafeClipDuration { get; }

    IReadOnlyList<RecordedDanmakuItem>? DanmakuItems { get; }

    string? DanmakuPath { get; }

    string SourceVideoCodec { get; }

    IClipExporter Exporter { get; }

    string EncoderText { get; }

    void SetStatusText(string text);

    void SetSelection(TimeSpan start, TimeSpan end);
}

/// <summary>
/// 剪辑台「切片导出」面板子视图模型：导出模式、输出目录/文件名、导出队列与取消。
/// 从 <see cref="ClipStudioViewModel"/> 抽离，视频/选区/边录边剪状态经 <see cref="IClipExportHost"/> 读取；
/// 本类不引用 Avalonia 类型，可独立单测。
/// </summary>
public sealed partial class ClipExportViewModel : ObservableObject, IDisposable
{
    private const int MaxExportTasks = 6;

    private readonly IClipExportHost _host;
    private readonly IAppSettingsRepository _settings;

    private bool _disposed;
    private CancellationTokenSource? _exportCts;
    private ClipExportMode _exportMode = ClipExportMode.Both;
    private string _outputDirectory = string.Empty;
    private string _outputFileName = string.Empty;
    private bool _outputFileNameAuto = true;
    private bool _settingAutoOutputFileName;
    private bool _isExporting;

    internal ClipExportViewModel(IClipExportHost host, IAppSettingsRepository settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>由 View 注入：选择切片导出目录，返回绝对路径；null 表示取消。</summary>
    public Func<Task<string?>>? PickExportDirectoryAsync { get; set; }

    /// <summary>测试接缝：真正执行「打开输出目录」的动作；默认调用资源管理器打开目录。</summary>
    internal Action<string> OpenDirectory { get; set; } = OpenDirectoryInExplorer;

    public ObservableCollection<ClipExportTaskViewModel> ExportTasks { get; } = new();

    /// <summary>已探测到的编码器文案；只读转发宿主，编码器变化时由宿主调用 <see cref="NotifyEncoderTextChanged"/> 刷新绑定。</summary>
    public string EncoderText => _host.EncoderText;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetProperty(ref _outputDirectory, value))
            {
                NotifyAvailabilityChanged();
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

            NotifyAvailabilityChanged();
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
                NotifyAvailabilityChanged();
                OnPropertyChanged(nameof(ExportButtonText));
            }
        }
    }

    public string ExportButtonText => _isExporting ? "导出中…" : "开始导出 MP4";

    public bool CanExport =>
        _host.HasVideo && !IsExporting
        && _host.OutPoint - _host.InPoint >= _host.SelectionMinimum
        && !string.IsNullOrWhiteSpace(_outputDirectory)
        && !string.IsNullOrWhiteSpace(_outputFileName);

    /// <summary>关键帧对齐误差提示（无损切割可能提前 1~3 秒）。</summary>
    public string KeyframeTip => "无弹幕版使用 -c copy 无损切割，入点会自动对齐到最近关键帧，可能提前 1~3 秒。";

    /// <summary>由宿主在初始化时调用：按配置解析默认导出目录。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        OutputDirectory = await ResolveDefaultOutputDirectoryAsync(ct).ConfigureAwait(true);
    }

    /// <summary>加载新视频后调用：按视频路径与选区强制生成默认文件名（原逻辑会覆盖用户手改值）。</summary>
    public void SetGeneratedOutputFileName(string videoPath, TimeSpan start, TimeSpan end)
    {
        SetAutoOutputFileName(BuildDefaultOutputName(videoPath, start, end));
    }

    /// <summary>选区变化后调用：仅在用户未手改文件名时按选区刷新默认文件名。</summary>
    public void UpdateGeneratedOutputFileName(string videoPath, TimeSpan start, TimeSpan end)
    {
        if (!_outputFileNameAuto)
        {
            return;
        }

        SetAutoOutputFileName(BuildDefaultOutputName(videoPath, start, end));
    }

    /// <summary>宿主编码器文案变化后调用：刷新「编码器」只读绑定的显示。</summary>
    public void NotifyEncoderTextChanged() => OnPropertyChanged(nameof(EncoderText));

    /// <summary>宿主的视频/选区/边录边剪状态变化后调用：刷新导出按钮可用性。</summary>
    public void NotifyAvailabilityChanged() => OnPropertyChanged(nameof(CanExport));

    [RelayCommand]
    private async Task ExportAsync()
    {
        var videoPath = _host.CurrentVideoPath;
        if (!CanExport || videoPath is null)
        {
            _host.SetStatusText("请先加载视频、设置有效入出点与输出目录。");
            return;
        }

        // 边录边剪且安全缓冲尚未成型（已录不足 5 秒）：此时导出可能读到半截写入的数据。
        var safeClipDuration = _host.SafeClipDuration;
        if (_host.IsLiveSession && safeClipDuration <= TimeSpan.Zero)
        {
            _host.SetStatusText("安全缓冲生成中（已录不足 5 秒），请稍后再导出。");
            return;
        }

        // 边录边剪：录制结束前保留 5 秒安全缓冲，导出终点自动钳制到安全截止时间。
        if (_host.IsLiveSession && safeClipDuration > TimeSpan.Zero)
        {
            if (_host.InPoint >= safeClipDuration)
            {
                _host.SetStatusText("当前入点已超过安全可剪范围，请把入点移到录制进度稍早的位置。");
                return;
            }

            if (_host.OutPoint > safeClipDuration)
            {
                _host.SetSelection(_host.InPoint, safeClipDuration);
                _host.SetStatusText("已自动保留 5 秒安全缓冲避免影响录制。");
            }
        }

        var danmakuItems = _host.DanmakuItems;
        var request = new ClipExportRequest(
            videoPath,
            danmakuItems is { Count: > 0 } ? _host.DanmakuPath : null,
            _outputDirectory,
            SanitizeFileName(_outputFileName),
            _host.InPoint,
            _host.OutPoint,
            _exportMode);

        var task = new ClipExportTaskViewModel(request, danmakuItems, _host.SourceVideoCodec, _host.Exporter);
        ExportTasks.Insert(0, task);
        while (ExportTasks.Count > MaxExportTasks)
        {
            ExportTasks.RemoveAt(ExportTasks.Count - 1);
        }

        IsExporting = true;
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        _host.SetStatusText($"开始导出：{task.RangeText} · {task.ModeText}");
        try
        {
            await task.RunAsync(_exportCts.Token).ConfigureAwait(true);
        }
        finally
        {
            IsExporting = false;
        }

        _host.SetStatusText(task.IsCompleted
            ? $"导出完成：{task.OutputText}"
            : task.IsCanceled
                ? "导出已取消。"
                : $"导出失败：{task.ErrorMessage}");
    }

    [RelayCommand]
    private void CancelExport() => CancelCurrentExport();

    public void CancelCurrentExport()
    {
        if (_exportCts is null || !_isExporting)
        {
            return;
        }

        _host.SetStatusText("正在取消导出…");
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
            _host.SetStatusText("当前界面未接入目录选择器。");
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
            _host.SetStatusText($"选择导出目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory) || !Directory.Exists(_outputDirectory))
        {
            _host.SetStatusText("输出目录尚不存在，导出时会自动创建。");
            return;
        }

        try
        {
            OpenDirectory(_outputDirectory);
        }
        catch (Exception ex)
        {
            _host.SetStatusText($"打开输出目录失败：{ex.Message}。请手动在资源管理器中打开：{_outputDirectory}");
        }
    }

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

    private static void OpenDirectoryInExplorer(string directory)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
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
            _exportCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _exportCts?.Dispose();
        _exportCts = null;
        ExportTasks.Clear();
    }
}
