using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 剪辑台导出子视图模型的纯单测：Fake Host + Fake Exporter，不触碰 Avalonia、ffmpeg 与资源管理器。
/// 覆盖导出按钮门禁、自动命名、模式互斥、边录边剪守卫/钳制、成功入队与队列上限、取消、
/// 目录选择/打开输出目录的各类文案、Dispose 幂等与属性通知。
/// </summary>
public sealed class ClipExportViewModelTests
{
    private static readonly TimeSpan DefaultInPoint = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultOutPoint = TimeSpan.FromSeconds(40);

    // ---- CanExport 门禁 ----

    [Fact]
    public void CanExport_WithValidState_IsTrue()
    {
        using var viewModel = CreateViewModel();

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public void CanExport_WithoutVideo_IsFalse()
    {
        var host = new FakeClipExportHost { CurrentVideoPath = null, HasVideo = false };
        using var viewModel = CreateViewModel(host);

        Assert.False(viewModel.CanExport);

        host.CurrentVideoPath = "clip.mp4";
        host.HasVideo = true;
        viewModel.NotifyAvailabilityChanged();

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public void CanExport_WithTooShortSelection_IsFalse()
    {
        var host = new FakeClipExportHost
        {
            InPoint = TimeSpan.Zero,
            OutPoint = TimeSpan.FromMilliseconds(100)
        };
        using var viewModel = CreateViewModel(host);

        Assert.False(viewModel.CanExport);

        host.OutPoint = DefaultOutPoint;
        viewModel.NotifyAvailabilityChanged();

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public void CanExport_WithoutOutputDirectory_IsFalse()
    {
        using var viewModel = CreateViewModel();
        viewModel.OutputDirectory = "  ";

        Assert.False(viewModel.CanExport);

        viewModel.OutputDirectory = @"C:\fake-output";

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public void CanExport_WithoutOutputFileName_IsFalse()
    {
        using var viewModel = CreateViewModel();
        viewModel.OutputFileName = string.Empty;

        Assert.False(viewModel.CanExport);

        viewModel.OutputFileName = "clip";

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public async Task CanExport_WhileExporting_IsFalse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new ScriptedClipExporter
        {
            Handler = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Array.Empty<string>();
            }
        };
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);

        var running = viewModel.ExportCommand.ExecuteAsync(null);
        await started.Task;

        Assert.True(viewModel.IsExporting);
        Assert.False(viewModel.CanExport);
        Assert.Equal("导出中…", viewModel.ExportButtonText);

        viewModel.CancelExportCommand.Execute(null);
        await running;

        Assert.False(viewModel.IsExporting);
        Assert.Equal("开始导出 MP4", viewModel.ExportButtonText);
    }

    // ---- 输出文件名：自动命名与手改保护 ----

    [Fact]
    public void OutputFileName_ManualEdit_DisablesAutoNaming()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host, configureOutput: false);
        viewModel.OutputDirectory = @"C:\fake-output";

        viewModel.SetGeneratedOutputFileName(@"C:\videos\rec.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(30));
        Assert.Equal("rec_00.00-00.30", viewModel.OutputFileName);

        viewModel.OutputFileName = "我的切片";
        viewModel.UpdateGeneratedOutputFileName(@"C:\videos\rec.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(45));

        Assert.Equal("我的切片", viewModel.OutputFileName);
    }

    [Fact]
    public void SetGeneratedOutputFileName_ForceOverwritesAndRestoresAutoNaming()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host, configureOutput: false);
        viewModel.OutputDirectory = @"C:\fake-output";

        viewModel.OutputFileName = "我的切片";
        viewModel.UpdateGeneratedOutputFileName(@"C:\videos\rec.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(45));
        Assert.Equal("我的切片", viewModel.OutputFileName);

        viewModel.SetGeneratedOutputFileName(@"C:\videos\rec.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(45));
        Assert.Equal(@"rec_00.00-00.45", viewModel.OutputFileName);

        viewModel.UpdateGeneratedOutputFileName(@"C:\videos\rec.mkv", TimeSpan.Zero, TimeSpan.FromSeconds(60));
        Assert.Equal("rec_00.00-01.00", viewModel.OutputFileName);
    }

    // ---- 导出模式 ----

    [Fact]
    public void ExportModes_AreMutuallyExclusive()
    {
        using var viewModel = CreateViewModel();

        Assert.True(viewModel.IsBothExport);
        Assert.Equal(ClipExportMode.Both, viewModel.ExportMode);

        viewModel.IsCleanOnlyExport = true;
        Assert.True(viewModel.IsCleanOnlyExport);
        Assert.False(viewModel.IsDanmakuOnlyExport);
        Assert.False(viewModel.IsBothExport);
        Assert.Equal(ClipExportMode.CleanOnly, viewModel.ExportMode);

        viewModel.IsDanmakuOnlyExport = true;
        Assert.False(viewModel.IsCleanOnlyExport);
        Assert.True(viewModel.IsDanmakuOnlyExport);
        Assert.False(viewModel.IsBothExport);
        Assert.Equal(ClipExportMode.BurnedDanmaku, viewModel.ExportMode);

        viewModel.IsBothExport = true;
        Assert.False(viewModel.IsCleanOnlyExport);
        Assert.False(viewModel.IsDanmakuOnlyExport);
        Assert.True(viewModel.IsBothExport);
        Assert.Equal(ClipExportMode.Both, viewModel.ExportMode);

        // RadioButton 取消勾选（isChecked=false）不应改变模式。
        viewModel.IsCleanOnlyExport = false;
        Assert.Equal(ClipExportMode.Both, viewModel.ExportMode);
        Assert.False(viewModel.IsCleanOnlyExport);
    }

    // ---- 导出守卫 ----

    [Fact]
    public async Task Export_WithoutVideo_ShowsGuardText()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost
        {
            CurrentVideoPath = null,
            HasVideo = false,
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Empty(exporter.Calls);
        Assert.Empty(viewModel.ExportTasks);
        Assert.Equal("请先加载视频、设置有效入出点与输出目录。", host.LastStatus);
    }

    [Fact]
    public async Task Export_LiveSessionWithoutSafetyBuffer_ShowsGuardText()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost
        {
            IsLiveSession = true,
            SafeClipDuration = TimeSpan.Zero,
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Empty(exporter.Calls);
        Assert.Equal("安全缓冲生成中（已录不足 5 秒），请稍后再导出。", host.LastStatus);
    }

    [Fact]
    public async Task Export_LiveSessionInPointBeyondSafe_ShowsGuardText()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost
        {
            IsLiveSession = true,
            SafeClipDuration = TimeSpan.FromSeconds(7),
            InPoint = TimeSpan.FromSeconds(8),
            OutPoint = TimeSpan.FromSeconds(12),
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Empty(exporter.Calls);
        Assert.Empty(host.Selections);
        Assert.Equal("当前入点已超过安全可剪范围，请把入点移到录制进度稍早的位置。", host.LastStatus);
    }

    [Fact]
    public async Task Export_LiveSessionOutPointBeyondSafe_ClampsSelectionThroughHost()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost
        {
            IsLiveSession = true,
            SafeClipDuration = TimeSpan.FromSeconds(7),
            InPoint = TimeSpan.Zero,
            OutPoint = TimeSpan.FromSeconds(11),
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal((TimeSpan.Zero, TimeSpan.FromSeconds(7)), Assert.Single(host.Selections));
        Assert.Contains(host.StatusTexts, text => text == "已自动保留 5 秒安全缓冲避免影响录制。");
        var task = Assert.Single(viewModel.ExportTasks);
        Assert.Equal(TimeSpan.Zero, task.Request.StartTime);
        Assert.Equal(TimeSpan.FromSeconds(7), task.Request.EndTime);
        Assert.True(task.IsCompleted);
    }

    // ---- 成功导出与队列 ----

    [Fact]
    public async Task Export_Success_InsertsTaskAtFrontAndCompletes()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        var task = Assert.Single(viewModel.ExportTasks);
        Assert.True(task.IsCompleted);
        Assert.False(viewModel.IsExporting);
        Assert.Equal("clip.mp4", task.Request.InputVideoPath);
        Assert.Equal(@"C:\fake-output", task.Request.OutputDirectory);
        Assert.Equal("clip_10s-40s", task.Request.OutputBaseName);
        Assert.Equal(DefaultInPoint, task.Request.StartTime);
        Assert.Equal(DefaultOutPoint, task.Request.EndTime);
        Assert.Equal(ClipExportMode.Both, task.Request.Mode);
        Assert.Null(task.Request.CompanionDanmakuPath);
        Assert.Contains(host.StatusTexts, text => text == "开始导出：00:10 - 00:40 · 双版");
        Assert.Equal("导出完成：clip_10s-40s.mp4", host.LastStatus);
        Assert.Equal("开始导出 MP4", viewModel.ExportButtonText);
    }

    [Fact]
    public async Task Export_QueueIsTrimmedToMaxSix()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);

        for (var i = 0; i < 7; i++)
        {
            viewModel.OutputFileName = $"clip_{i}";
            await viewModel.ExportCommand.ExecuteAsync(null);
        }

        Assert.Equal(6, viewModel.ExportTasks.Count);
        Assert.Equal("clip_6", viewModel.ExportTasks[0].Request.OutputBaseName);
        Assert.Equal("clip_1", viewModel.ExportTasks[5].Request.OutputBaseName);
    }

    [Fact]
    public async Task Export_PassesDanmakuAndCodecToExporter()
    {
        var exporter = new ScriptedClipExporter();
        IReadOnlyList<RecordedDanmakuItem> danmakuItems = new[]
        {
            new RecordedDanmakuItem(1000, "观众甲", "你好", false, false)
        };
        var host = new FakeClipExportHost
        {
            DanmakuItems = danmakuItems,
            DanmakuPath = @"C:\videos\clip.jsonl",
            SourceVideoCodec = "hevc",
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        var call = Assert.Single(exporter.Calls);
        Assert.Equal(@"C:\videos\clip.jsonl", call.Request.CompanionDanmakuPath);
        Assert.Equal("hevc", call.SourceVideoCodec);
        Assert.Same(danmakuItems, call.DanmakuItems);
    }

    [Fact]
    public async Task Export_WithoutDanmaku_PassesNullCompanionPath()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost
        {
            DanmakuItems = Array.Empty<RecordedDanmakuItem>(),
            DanmakuPath = @"C:\videos\clip.jsonl",
            Exporter = exporter
        };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(exporter.Calls).Request.CompanionDanmakuPath);
    }

    [Fact]
    public async Task Export_SanitizesInvalidFileNameChars()
    {
        var exporter = new ScriptedClipExporter();
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);
        var invalid = Path.GetInvalidFileNameChars()
            .First(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch));
        viewModel.OutputFileName = $"clip{invalid}name";

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal("clip_name", Assert.Single(exporter.Calls).Request.OutputBaseName);
    }

    [Fact]
    public async Task Export_Failure_ReportsErrorMessage()
    {
        var exporter = new ScriptedClipExporter
        {
            Handler = (_, _) => Task.FromException<IReadOnlyList<string>>(
                new InvalidOperationException("ffmpeg 崩溃"))
        };
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);

        await viewModel.ExportCommand.ExecuteAsync(null);

        var task = Assert.Single(viewModel.ExportTasks);
        Assert.True(task.IsFailed);
        Assert.False(viewModel.IsExporting);
        Assert.Equal("导出失败：ffmpeg 崩溃", host.LastStatus);
    }

    // ---- 取消 ----

    [Fact]
    public async Task CancelExportCommand_CancelsRunningExport()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new ScriptedClipExporter
        {
            Handler = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Array.Empty<string>();
            }
        };
        var host = new FakeClipExportHost { Exporter = exporter };
        using var viewModel = CreateViewModel(host);

        var running = viewModel.ExportCommand.ExecuteAsync(null);
        await started.Task;

        viewModel.CancelExportCommand.Execute(null);
        await running;

        var task = Assert.Single(viewModel.ExportTasks);
        Assert.True(task.IsCanceled);
        Assert.False(viewModel.IsExporting);
        Assert.Equal("正在取消导出…", host.StatusTexts[^2]);
        Assert.Equal("导出已取消。", host.LastStatus);
    }

    [Fact]
    public void CancelCurrentExport_WhenIdle_DoesNothing()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);

        viewModel.CancelCurrentExport();
        viewModel.CancelExportCommand.Execute(null);

        Assert.Empty(host.StatusTexts);
    }

    // ---- 目录选择器 ----

    [Fact]
    public async Task PickOutputDirectory_WithoutSelector_ShowsNotWired()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);
        viewModel.PickExportDirectoryAsync = null;

        await viewModel.PickOutputDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("当前界面未接入目录选择器。", host.LastStatus);
        Assert.Equal(@"C:\fake-output", viewModel.OutputDirectory);
    }

    [Fact]
    public async Task PickOutputDirectory_Cancel_KeepsExistingDirectory()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);
        viewModel.PickExportDirectoryAsync = () => Task.FromResult<string?>(null);

        await viewModel.PickOutputDirectoryCommand.ExecuteAsync(null);

        Assert.Empty(host.StatusTexts);
        Assert.Equal(@"C:\fake-output", viewModel.OutputDirectory);
    }

    [Fact]
    public async Task PickOutputDirectory_Success_UpdatesDirectory()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);
        viewModel.PickExportDirectoryAsync = () => Task.FromResult<string?>(@"D:\exports");

        await viewModel.PickOutputDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\exports", viewModel.OutputDirectory);
        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public async Task PickOutputDirectory_Throws_ShowsError()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);
        viewModel.PickExportDirectoryAsync = () => throw new InvalidOperationException("boom");

        await viewModel.PickOutputDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("选择导出目录失败：boom", host.LastStatus);
    }

    // ---- 打开输出目录 ----

    [Fact]
    public async Task OpenOutputDirectory_MissingDirectory_ShowsTip()
    {
        var host = new FakeClipExportHost();
        using var viewModel = CreateViewModel(host);
        viewModel.OutputDirectory = Path.Combine(
            Path.GetTempPath(),
            "limoclip-export-missing-" + Guid.NewGuid().ToString("N"));
        viewModel.OpenDirectory = _ => throw new InvalidOperationException("不应调用打开目录");

        await Task.Run(() => viewModel.OpenOutputDirectoryCommand.Execute(null));

        Assert.Equal("输出目录尚不存在，导出时会自动创建。", host.LastStatus);
    }

    [Fact]
    public async Task OpenOutputDirectory_Throws_ShowsFailureText()
    {
        var directory = CreateTempDirectory();
        try
        {
            var host = new FakeClipExportHost();
            using var viewModel = CreateViewModel(host);
            viewModel.OutputDirectory = directory;
            viewModel.OpenDirectory = _ => throw new InvalidOperationException("explorer boom");

            await Task.Run(() => viewModel.OpenOutputDirectoryCommand.Execute(null));

            Assert.Equal(
                $"打开输出目录失败：explorer boom。请手动在资源管理器中打开：{directory}",
                host.LastStatus);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpenOutputDirectory_ExistingDirectory_InvokesOpener()
    {
        var directory = CreateTempDirectory();
        try
        {
            var host = new FakeClipExportHost();
            using var viewModel = CreateViewModel(host);
            viewModel.OutputDirectory = directory;
            string? opened = null;
            viewModel.OpenDirectory = path => opened = path;

            await Task.Run(() => viewModel.OpenOutputDirectoryCommand.Execute(null));

            Assert.Equal(directory, opened);
            Assert.Empty(host.StatusTexts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Dispose / 属性通知 ----

    [Fact]
    public async Task Dispose_IsIdempotent_AndClearsQueue()
    {
        var host = new FakeClipExportHost();
        var viewModel = CreateViewModel(host);
        await viewModel.ExportCommand.ExecuteAsync(null);
        Assert.Single(viewModel.ExportTasks);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Empty(viewModel.ExportTasks);
    }

    [Fact]
    public void EncoderText_ForwardsHostValue_AndNotifies()
    {
        var host = new FakeClipExportHost { EncoderText = "libx264 · 软件编码" };
        using var viewModel = CreateViewModel(host);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("libx264 · 软件编码", viewModel.EncoderText);

        host.EncoderText = "hevc_nvenc · 硬件编码";
        viewModel.NotifyEncoderTextChanged();

        Assert.Equal("hevc_nvenc · 硬件编码", viewModel.EncoderText);
        Assert.Contains(nameof(ClipExportViewModel.EncoderText), raised);
    }

    [Fact]
    public void NotifyAvailabilityChanged_RaisesCanExportNotification()
    {
        using var viewModel = CreateViewModel();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.NotifyAvailabilityChanged();

        Assert.Contains(nameof(ClipExportViewModel.CanExport), raised);
    }

    [Fact]
    public async Task InitializeAsync_UsesConfiguredRecordingDirectory()
    {
        var settings = new FakeAppSettingsRepository();
        var configured = Path.Combine(
            Path.GetTempPath(),
            "limoclip-configured-" + Guid.NewGuid().ToString("N"));
        settings.Values[AppSettingKeys.RecordingDefaultDirectory] = configured;
        using var viewModel = new ClipExportViewModel(new FakeClipExportHost(), settings);

        await viewModel.InitializeAsync();

        Assert.Equal(configured, viewModel.OutputDirectory);
    }

    // ---- 测试基建 ----

    private static ClipExportViewModel CreateViewModel(
        FakeClipExportHost? host = null,
        IAppSettingsRepository? settings = null,
        bool configureOutput = true)
    {
        var viewModel = new ClipExportViewModel(
            host ?? new FakeClipExportHost(),
            settings ?? new FakeAppSettingsRepository());
        if (configureOutput)
        {
            viewModel.OutputDirectory = @"C:\fake-output";
            viewModel.OutputFileName = "clip_10s-40s";
        }

        return viewModel;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "limoclip-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeClipExportHost : IClipExportHost
    {
        public string? CurrentVideoPath { get; set; } = "clip.mp4";

        public bool HasVideo { get; set; } = true;

        public TimeSpan InPoint { get; set; } = DefaultInPoint;

        public TimeSpan OutPoint { get; set; } = DefaultOutPoint;

        public TimeSpan SelectionMinimum { get; set; } = TimeSpan.FromMilliseconds(200);

        public bool IsLiveSession { get; set; }

        public TimeSpan SafeClipDuration { get; set; }

        public IReadOnlyList<RecordedDanmakuItem>? DanmakuItems { get; set; }

        public string? DanmakuPath { get; set; }

        public string SourceVideoCodec { get; set; } = "h264";

        public IClipExporter Exporter { get; set; } = new ScriptedClipExporter();

        public string EncoderText { get; set; } = "自动探测中…";

        public List<string> StatusTexts { get; } = new();

        public List<(TimeSpan Start, TimeSpan End)> Selections { get; } = new();

        public string? LastStatus => StatusTexts.Count == 0 ? null : StatusTexts[^1];

        public void SetStatusText(string text) => StatusTexts.Add(text);

        public void SetSelection(TimeSpan start, TimeSpan end)
        {
            Selections.Add((start, end));
            InPoint = start;
            OutPoint = end;
        }
    }

    private sealed class ScriptedClipExporter : IClipExporter
    {
        public List<ExporterCall> Calls { get; } = new();

        public Func<ClipExportRequest, CancellationToken, Task<IReadOnlyList<string>>>? Handler { get; set; }

        public Task<IReadOnlyList<string>> ExportAsync(
            ClipExportRequest request,
            IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
            string sourceVideoCodec,
            IProgress<ClipExportProgress>? progress = null,
            CancellationToken ct = default)
        {
            Calls.Add(new ExporterCall(request, danmakuItems, sourceVideoCodec));
            if (Handler is not null)
            {
                return Handler(request, ct);
            }

            var output = Path.Combine(request.OutputDirectory, request.OutputBaseName + ".mp4");
            return Task.FromResult<IReadOnlyList<string>>(new[] { output });
        }
    }

    private sealed record ExporterCall(
        ClipExportRequest Request,
        IReadOnlyList<RecordedDanmakuItem>? DanmakuItems,
        string SourceVideoCodec);

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string?> Values { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
