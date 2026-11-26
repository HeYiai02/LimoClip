using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 剪辑台 ASR 子视图模型的纯单测：Fake Provider + Fake Host，不触碰 Avalonia、ffmpeg 与网络。
/// 覆盖选区/高能两个转写入口的守卫、成功回填、取消、异常、全局互斥、旧版本结果丢弃与复制提示。
/// </summary>
public sealed class ClipAsrViewModelTests
{
    private static readonly TimeSpan SelectionMinimum = TimeSpan.FromMilliseconds(200);

    // ---- 选区转写：守卫 ----

    [Fact]
    public async Task TranscribeSelection_WithoutVideo_ReportsUnavailable()
    {
        var provider = new ScriptedAsrProvider();
        var host = CreateHost(videoPath: null, duration: TimeSpan.Zero, inPoint: TimeSpan.Zero, outPoint: TimeSpan.Zero);
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(0, provider.CallCount);
        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.HasResult);
        Assert.Equal("请先打开本地视频", viewModel.StatusText);
        Assert.Equal("请先打开本地视频", viewModel.ErrorText);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task TranscribeSelection_WithoutDuration_ReportsUnavailable()
    {
        var provider = new ScriptedAsrProvider();
        var host = CreateHost(duration: TimeSpan.Zero, inPoint: TimeSpan.Zero, outPoint: TimeSpan.Zero);
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal("请先打开本地视频", viewModel.StatusText);
        Assert.Equal("请先打开本地视频", viewModel.ErrorText);
    }

    [Fact]
    public async Task TranscribeSelection_WithTooShortSelection_ShowsInvalidSelection()
    {
        var provider = new ScriptedAsrProvider();
        var host = CreateHost(inPoint: TimeSpan.Zero, outPoint: TimeSpan.FromMilliseconds(100));
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(0, provider.CallCount);
        Assert.False(viewModel.IsRunning);
        Assert.Equal("请先在时间轴上选择有效的入出点区间。", viewModel.ErrorText);
        Assert.Equal("选区无效", viewModel.StatusText);
        Assert.True(viewModel.HasError);
    }

    // ---- 选区转写：成功回填 ----

    [Fact]
    public async Task TranscribeSelection_Success_PopulatesPanel()
    {
        var host = CreateHost(inPoint: TimeSpan.FromSeconds(10), outPoint: TimeSpan.FromSeconds(40));
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                "你好 世界",
                new[] { Segment(0, 1, "你好"), Segment(1, 2, "世界") },
                "  whisper-large-v3-turbo  "))
        };
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("clip.mp4", provider.Calls[0].VideoPath);
        Assert.Equal(TimeSpan.FromSeconds(10), provider.Calls[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(40), provider.Calls[0].End);
        Assert.Equal(2, viewModel.TranscriptLines.Count);
        Assert.Equal("你好 世界", viewModel.FullText);
        Assert.Equal("whisper-large-v3-turbo", viewModel.ProviderLabel);
        Assert.Equal(
            $"{ClipExportTaskViewModel.FormatTime(TimeSpan.FromSeconds(10))} - {ClipExportTaskViewModel.FormatTime(TimeSpan.FromSeconds(40))}",
            viewModel.RangeText);
        Assert.True(viewModel.HasResult);
        Assert.False(viewModel.HasError);
        Assert.Null(viewModel.ErrorText);
        Assert.Equal("识别完成：2 句", viewModel.StatusText);
        Assert.False(viewModel.IsRunning);
        // 任务开始/结束各一次：高能卡片的转写按钮可用性随全局互斥刷新。
        Assert.True(host.RefreshHighlightCount >= 2);
    }

    [Fact]
    public async Task TranscribeSelection_WithoutSegments_UsesFullTextAsSingleLine()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                "纯文本结果",
                Array.Empty<AsrTranscriptSegment>()))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        var line = Assert.Single(viewModel.TranscriptLines);
        Assert.Equal("纯文本结果", line.Text);
        Assert.Equal(TimeSpan.Zero, line.RelativeStart);
        Assert.Equal("识别完成：1 句", viewModel.StatusText);
        Assert.True(viewModel.HasResult);
    }

    [Fact]
    public async Task TranscribeSelection_WithBlankSegments_SkipsThemAndJoinsLineTexts()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                string.Empty,
                new[] { Segment(0, 1, "第一句"), Segment(1, 2, "   "), Segment(2, 3, "第二句") }))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.TranscriptLines.Count);
        Assert.Equal($"第一句{Environment.NewLine}第二句", viewModel.FullText);
        Assert.Equal("识别完成：2 句", viewModel.StatusText);
    }

    [Fact]
    public async Task TranscribeSelection_WithBlankProviderLabel_ShowsUnknownProvider()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                "文本",
                segments: null,
                providerLabel: "   "))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal("未知服务商", viewModel.ProviderLabel);
    }

    [Fact]
    public async Task TranscribeSelection_WithEmptyResult_ShowsNoTextStatus()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                string.Empty,
                Array.Empty<AsrTranscriptSegment>()))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasResult);
        Assert.Empty(viewModel.TranscriptLines);
        Assert.Equal(string.Empty, viewModel.FullText);
        Assert.Equal("识别完成：未识别到文本", viewModel.StatusText);
        Assert.Null(viewModel.ErrorText);
    }

    // ---- 单句跳转 ----

    [Fact]
    public void SeekToLine_UsesResultClipStartPlusRelativeOffset()
    {
        var host = CreateHost();
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), host);
        var card = CreateCard(TimeSpan.FromSeconds(20));
        card.AsrResult = CreateResult(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            "回放文本",
            new[] { Segment(0, 1, "第一句"), Segment(1, 2, "第二句") });

        viewModel.ShowHighlightAsrCommand.Execute(card);
        viewModel.SeekToLine(viewModel.TranscriptLines[1]);

        Assert.Equal(TimeSpan.FromSeconds(6), Assert.Single(host.SeekCalls));
    }

    [Fact]
    public void SeekToLine_WithoutResultOrDuration_DoesNothing()
    {
        var host = CreateHost();
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), host);
        var line = new AsrTranscriptLineViewModel(Segment(0, 1, "孤立句子"));

        viewModel.SeekToLine(null);
        viewModel.SeekToLine(line);
        Assert.Empty(host.SeekCalls);

        var card = CreateCard(TimeSpan.FromSeconds(20));
        card.AsrResult = CreateResult(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            "回放文本",
            new[] { Segment(0, 1, "第一句") });
        viewModel.ShowHighlightAsrCommand.Execute(card);
        host.Duration = TimeSpan.Zero;

        viewModel.SeekToLine(viewModel.TranscriptLines[0]);
        Assert.Empty(host.SeekCalls);
    }

    // ---- 选区转写：异常、取消、互斥、旧结果 ----

    [Fact]
    public async Task TranscribeSelection_WhenProviderThrows_SetsErrorAndMainStatus()
    {
        var host = CreateHost();
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, _, _, _) => Task.FromException<AsrTranscriptResult>(
                new InvalidOperationException("  尚未配置语音识别 API  "))
        };
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.CallCount);
        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.HasResult);
        Assert.Equal("识别失败", viewModel.StatusText);
        Assert.Equal("尚未配置语音识别 API", viewModel.ErrorText);
        Assert.True(viewModel.HasError);
        Assert.Equal("语音识别失败：尚未配置语音识别 API", Assert.Single(host.StatusTexts));
    }

    [Fact]
    public async Task TranscribeSelection_WhenProviderThrowsEmptyMessage_UsesFallbackMessage()
    {
        var host = CreateHost();
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, _, _, _) => Task.FromException<AsrTranscriptResult>(
                new InvalidOperationException("   "))
        };
        using var viewModel = new ClipAsrViewModel(provider, host);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal("语音识别失败，请稍后重试。", viewModel.ErrorText);
        Assert.Equal("识别失败", viewModel.StatusText);
        Assert.Equal("语音识别失败：语音识别失败，请稍后重试。", Assert.Single(host.StatusTexts));
    }

    [Fact]
    public async Task CancelAsr_WhileRunning_ReportsCancelledAndClearsError()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedAsrProvider
        {
            Handler = async (_, _, _, ct) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return CreateResult(TimeSpan.Zero, TimeSpan.Zero, string.Empty);
            }
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        var running = viewModel.TranscribeSelectionCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(viewModel.IsRunning);

        viewModel.ErrorText = "临时错误";
        viewModel.CancelAsrCommand.Execute(null);
        Assert.Equal("正在取消…", viewModel.StatusText);

        await running;

        Assert.False(viewModel.IsRunning);
        Assert.Equal("已取消", viewModel.StatusText);
        Assert.Null(viewModel.ErrorText);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.HasResult);
        Assert.Empty(viewModel.TranscriptLines);
    }

    [Fact]
    public async Task TranscribeHighlight_WhileSelectionRunning_IsIgnored()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedAsrProvider
        {
            Handler = async (_, start, end, _) =>
            {
                started.TrySetResult(true);
                await gate.Task;
                return CreateResult(start, end, "选区结果", new[] { Segment(0, 1, "选区结果") });
            }
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));

        var running = viewModel.TranscribeSelectionCommand.ExecuteAsync(null);
        await started.Task;

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(card);

        Assert.Equal(1, provider.CallCount);
        Assert.Null(card.AsrResult);
        Assert.True(viewModel.IsRunning);

        gate.TrySetResult(true);
        await running;
        Assert.False(viewModel.IsRunning);
        Assert.Equal("识别完成：1 句", viewModel.StatusText);
    }

    [Fact]
    public async Task TranscribeSelection_WhileHighlightRunning_IsIgnored()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedAsrProvider
        {
            Handler = async (_, start, end, _) =>
            {
                started.TrySetResult(true);
                await gate.Task;
                return CreateResult(start, end, "高能结果", new[] { Segment(0, 1, "高能结果") });
            }
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));

        var running = viewModel.TranscribeHighlightCommand.ExecuteAsync(card);
        await started.Task;
        Assert.True(card.IsTranscribing);

        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.CallCount);
        Assert.True(viewModel.IsRunning);

        gate.TrySetResult(true);
        await running;
        Assert.NotNull(card.AsrResult);
        Assert.False(card.IsTranscribing);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task Reset_WhileRunning_DiscardsStaleResult()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedAsrProvider
        {
            Handler = async (_, start, end, _) =>
            {
                started.TrySetResult(true);
                await gate.Task;
                return CreateResult(start, end, "旧结果", new[] { Segment(0, 1, "旧结果") });
            }
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        var running = viewModel.TranscribeSelectionCommand.ExecuteAsync(null);
        await started.Task;
        viewModel.Reset();
        Assert.Equal("尚未识别", viewModel.StatusText);

        gate.TrySetResult(true);
        await running;

        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.HasResult);
        Assert.Empty(viewModel.TranscriptLines);
        Assert.Equal(string.Empty, viewModel.FullText);
        Assert.Equal("尚未识别", viewModel.StatusText);
    }

    // ---- 高能片段转写 ----

    [Fact]
    public async Task TranscribeHighlight_RequestsLeadTailWindow_AndFillsCard()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                "高能文本",
                new[] { Segment(0, 2, "高能文本") }))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(card);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(10), provider.Calls[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(40), provider.Calls[0].End);
        Assert.NotNull(card.AsrResult);
        Assert.True(card.HasAsrResult);
        Assert.False(card.IsTranscribing);
        Assert.Equal("高能文本", viewModel.FullText);
        Assert.True(viewModel.HasResult);
        Assert.Equal("识别完成：1 句", viewModel.StatusText);
    }

    [Fact]
    public async Task TranscribeHighlight_ClampsWindowToVideoBounds()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(start, end, string.Empty))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        var head = CreateCard(TimeSpan.FromSeconds(5));
        var tail = CreateCard(TimeSpan.FromSeconds(55));

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(head);
        await viewModel.TranscribeHighlightCommand.ExecuteAsync(tail);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(TimeSpan.Zero, provider.Calls[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(25), provider.Calls[0].End);
        Assert.Equal(TimeSpan.FromSeconds(45), provider.Calls[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(60), provider.Calls[1].End);
    }

    [Fact]
    public async Task TranscribeHighlight_WithTooShortWindow_SetsCardError()
    {
        var provider = new ScriptedAsrProvider();
        var host = CreateHost(
            duration: TimeSpan.FromMilliseconds(150),
            inPoint: TimeSpan.Zero,
            outPoint: TimeSpan.FromMilliseconds(150));
        using var viewModel = new ClipAsrViewModel(provider, host);
        var card = CreateCard(TimeSpan.FromMilliseconds(50));

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(card);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal("高能片段过短，无法转写。", card.AsrError);
        Assert.True(card.HasAsrError);
        Assert.False(card.IsTranscribing);
        Assert.Equal("高能片段过短，无法转写。", viewModel.ErrorText);
        Assert.Equal("片段过短，无法转写", viewModel.StatusText);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task TranscribeHighlight_WithExistingResult_DoesNotCallProvider()
    {
        var provider = new ScriptedAsrProvider();
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));
        card.AsrResult = CreateResult(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(40),
            "已有结果",
            new[] { Segment(0, 1, "已有结果") });

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(card);

        Assert.Equal(0, provider.CallCount);
        Assert.True(viewModel.HasResult);
        Assert.Equal("已有结果", viewModel.FullText);
        Assert.Single(viewModel.TranscriptLines);
        Assert.Equal("识别完成：1 句", viewModel.StatusText);
    }

    [Fact]
    public async Task TranscribeHighlight_WithNullItem_DoesNothing()
    {
        var provider = new ScriptedAsrProvider();
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());

        await viewModel.TranscribeHighlightCommand.ExecuteAsync(null);
        viewModel.ShowHighlightAsrCommand.Execute(null);

        Assert.Equal(0, provider.CallCount);
        Assert.False(viewModel.HasResult);
        Assert.Equal("尚未识别", viewModel.StatusText);
    }

    // ---- 卡片摘要展开 ----

    [Fact]
    public void ShowHighlightAsr_ExpandsResultAndShowsPanel()
    {
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));
        card.AsrResult = CreateResult(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            "展开文本",
            new[] { Segment(0, 1, "展开文本") });
        viewModel.IsPanelVisible = false;

        viewModel.ShowHighlightAsrCommand.Execute(card);

        Assert.True(viewModel.IsPanelVisible);
        Assert.True(viewModel.HasResult);
        Assert.Equal("展开文本", viewModel.FullText);
        Assert.Single(viewModel.TranscriptLines);
        Assert.Equal("识别完成：1 句", viewModel.StatusText);
    }

    [Fact]
    public void ShowHighlightAsr_WithoutResult_DoesNothing()
    {
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), CreateHost());
        var card = CreateCard(TimeSpan.FromSeconds(20));
        viewModel.IsPanelVisible = false;

        viewModel.ShowHighlightAsrCommand.Execute(card);

        Assert.False(viewModel.IsPanelVisible);
        Assert.False(viewModel.HasResult);
        Assert.Equal("尚未识别", viewModel.StatusText);
    }

    // ---- 复制提示 ----

    [Fact]
    public void NotifyTextCopied_SetsStatusAndClearsError()
    {
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), CreateHost());
        viewModel.NotifyCopyFailed("磁盘不可写");
        Assert.True(viewModel.HasError);

        viewModel.NotifyTextCopied();

        Assert.Equal("已复制到剪贴板", viewModel.StatusText);
        Assert.Null(viewModel.ErrorText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public void NotifyCopyFailed_SetsErrorText()
    {
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), CreateHost());

        viewModel.NotifyCopyFailed("剪贴板被占用");

        Assert.Equal("复制识别文本失败：剪贴板被占用", viewModel.ErrorText);
        Assert.True(viewModel.HasError);
        Assert.Equal("尚未识别", viewModel.StatusText);
    }

    // ---- 重置 / 释放 ----

    [Fact]
    public async Task Reset_ClearsPanelAndKeepsAvailability()
    {
        var provider = new ScriptedAsrProvider
        {
            Handler = (_, start, end, _) => Task.FromResult(CreateResult(
                start,
                end,
                "重置前文本",
                new[] { Segment(0, 1, "重置前文本") }))
        };
        using var viewModel = new ClipAsrViewModel(provider, CreateHost());
        await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasResult);

        viewModel.Reset();

        Assert.False(viewModel.HasResult);
        Assert.Empty(viewModel.TranscriptLines);
        Assert.Equal(string.Empty, viewModel.FullText);
        Assert.Equal(string.Empty, viewModel.ProviderLabel);
        Assert.Equal(string.Empty, viewModel.RangeText);
        Assert.Null(viewModel.ErrorText);
        Assert.Equal("尚未识别", viewModel.StatusText);
        Assert.True(viewModel.CanTranscribeSelection);
        Assert.True(viewModel.TranscribeSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), CreateHost());

        viewModel.Dispose();
        viewModel.Dispose();
    }

    [Fact]
    public async Task Dispose_WhileRunning_CancelsWithoutThrowing()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedAsrProvider
        {
            Handler = async (_, _, _, ct) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return CreateResult(TimeSpan.Zero, TimeSpan.Zero, string.Empty);
            }
        };
        var viewModel = new ClipAsrViewModel(provider, CreateHost());

        var running = viewModel.TranscribeSelectionCommand.ExecuteAsync(null);
        await started.Task;

        viewModel.Dispose();
        await running;

        Assert.False(viewModel.IsRunning);
    }

    // ---- 可用性通知 ----

    [Fact]
    public void CanTranscribeSelection_TracksHostState()
    {
        var host = CreateHost();
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), host);

        Assert.True(viewModel.CanTranscribeSelection);

        host.HasVideo = false;
        viewModel.NotifyAvailabilityChanged();
        Assert.False(viewModel.CanTranscribeSelection);
        Assert.False(viewModel.TranscribeSelectionCommand.CanExecute(null));

        host.HasVideo = true;
        host.InPoint = TimeSpan.FromSeconds(10);
        host.OutPoint = TimeSpan.FromMilliseconds(10100);
        viewModel.NotifyAvailabilityChanged();
        Assert.False(viewModel.CanTranscribeSelection);

        host.OutPoint = TimeSpan.FromSeconds(40);
        viewModel.NotifyAvailabilityChanged();
        Assert.True(viewModel.CanTranscribeSelection);
        Assert.True(viewModel.TranscribeSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void NotifyAvailabilityChanged_RaisesBindingNotifications()
    {
        var host = CreateHost();
        using var viewModel = new ClipAsrViewModel(new ScriptedAsrProvider(), host);
        var propertyNames = new List<string>();
        viewModel.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName ?? string.Empty);
        var canExecuteChanged = false;
        viewModel.TranscribeSelectionCommand.CanExecuteChanged += (_, _) => canExecuteChanged = true;

        host.HasVideo = false;
        viewModel.NotifyAvailabilityChanged();

        Assert.Contains(nameof(ClipAsrViewModel.CanTranscribeSelection), propertyNames);
        Assert.True(canExecuteChanged);
        Assert.False(viewModel.CanTranscribeSelection);
    }

    // ---- 测试基建 ----

    private static FakeClipAsrHost CreateHost(
        string? videoPath = "clip.mp4",
        TimeSpan? duration = null,
        TimeSpan? inPoint = null,
        TimeSpan? outPoint = null)
    {
        return new FakeClipAsrHost
        {
            CurrentVideoPath = videoPath,
            Duration = duration ?? TimeSpan.FromSeconds(60),
            HasVideo = videoPath is not null,
            InPoint = inPoint ?? TimeSpan.FromSeconds(10),
            OutPoint = outPoint ?? TimeSpan.FromSeconds(40),
            SelectionMinimum = SelectionMinimum
        };
    }

    private static ClipHighlightItemViewModel CreateCard(TimeSpan position) =>
        new(
            new HighlightEvent(
                PlatformType.Bilibili,
                "room-asr",
                DateTimeOffset.UtcNow,
                18.75,
                3.5,
                "弹幕速率突增"),
            position);

    private static AsrTranscriptSegment Segment(double startSeconds, double endSeconds, string text) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), text);

    private static AsrTranscriptResult CreateResult(
        TimeSpan clipStart,
        TimeSpan clipEnd,
        string fullText,
        IReadOnlyList<AsrTranscriptSegment>? segments = null,
        string providerLabel = "fake") =>
        new(
            clipStart,
            clipEnd,
            fullText,
            segments ?? Array.Empty<AsrTranscriptSegment>(),
            providerLabel,
            DateTimeOffset.UtcNow);

    private sealed record AsrCall(string VideoPath, TimeSpan Start, TimeSpan End);

    /// <summary>可配置返回值/异常并记录调用参数的 ASR Fake：测试不发起任何真实网络请求。</summary>
    private sealed class ScriptedAsrProvider : IAsrProvider
    {
        public List<AsrCall> Calls { get; } = new();

        public int CallCount => Calls.Count;

        /// <summary>四参数 Handler（含 CancellationToken），便于模拟取消与阻塞中的转写。</summary>
        public Func<string, TimeSpan, TimeSpan, CancellationToken, Task<AsrTranscriptResult>>? Handler { get; set; }

        public Task<AsrTranscriptResult> TranscribeSegmentAsync(
            string videoPath,
            TimeSpan startTime,
            TimeSpan endTime,
            CancellationToken ct = default)
        {
            Calls.Add(new AsrCall(videoPath, startTime, endTime));
            return Handler is not null
                ? Handler(videoPath, startTime, endTime, ct)
                : Task.FromResult(CreateResult(startTime, endTime, string.Empty));
        }
    }

    private sealed class FakeClipAsrHost : IClipAsrHost
    {
        public string? CurrentVideoPath { get; set; }

        public TimeSpan Duration { get; set; }

        public TimeSpan InPoint { get; set; }

        public TimeSpan OutPoint { get; set; }

        public bool HasVideo { get; set; }

        public TimeSpan SelectionMinimum { get; set; }

        public List<TimeSpan> SeekCalls { get; } = new();

        public List<string> StatusTexts { get; } = new();

        public int RefreshHighlightCount { get; private set; }

        public void SeekTo(TimeSpan position) => SeekCalls.Add(position);

        public void SetStatusText(string text) => StatusTexts.Add(text);

        public void RefreshHighlightAvailability() => RefreshHighlightCount++;
    }
}
