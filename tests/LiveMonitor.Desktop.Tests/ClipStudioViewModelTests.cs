using System.Diagnostics;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Media.Editing;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 剪辑台 ViewModel 的本地文件加载路径测试。真实 VideoProbeService（ffmpeg 探测 mp4 元数据）
/// + 其余依赖用内存 Fake，避免拉起播放器/GUI。
/// </summary>
public sealed class ClipStudioViewModelTests
{
    [Fact]
    public async Task OpenLocalFile_LoadsMetadata_AndDegradesWithoutCompanionDanmaku()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return; // 与 Media 测试一致：缺 ffmpeg 时跳过。
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "local-test.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var viewModel = CreateViewModel(ffmpegPath);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);

                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);

                Assert.Equal(videoPath, viewModel.CurrentVideoPath);
                Assert.Null(viewModel.CurrentDanmakuPath);
                Assert.True(viewModel.Duration >= TimeSpan.FromSeconds(1.5) && viewModel.Duration <= TimeSpan.FromSeconds(2.5),
                    $"探测时长异常：{viewModel.Duration}");
                Assert.False(viewModel.HasDanmaku);
                Assert.Equal("无伴随弹幕文件", viewModel.DanmakuInfoText);
                Assert.Equal(TimeSpan.Zero, viewModel.InPoint);
                Assert.Equal(viewModel.Duration, viewModel.OutPoint);
                Assert.Contains("已加载", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task OpenSession_LoadsCompanionDanmaku_AndHighlightRecommendations()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "session-test.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var jsonlPath = Path.Combine(tempDirectory, "session-test.jsonl");
            await File.WriteAllLinesAsync(jsonlPath, new[]
            {
                "{\"type\":\"meta\",\"version\":1}",
                "{\"type\":\"dm\",\"offsetMs\":500,\"uname\":\"甲\",\"text\":\"第一条\"}",
                "{\"type\":\"dm\",\"offsetMs\":1500,\"uname\":\"乙\",\"text\":\"第二条\"}"
            });

            var session = new RecordingSession
            {
                Platform = PlatformType.Bilibili,
                RoomId = "room-1",
                RoomDisplayName = "测试房间",
                VideoFilePath = videoPath,
                DanmakuFilePath = jsonlPath,
                StartedAt = DateTime.UtcNow,
                Status = RecordingStatus.Completed
            };

            var highlights = new FakeHighlightEventRepository
            {
                Events =
                {
                    new HighlightEvent(PlatformType.Bilibili, "room-1", DateTimeOffset.UtcNow, 18.75, 3.5, "弹幕速率突增")
                    {
                        RecordingSessionId = session.Id,
                        RecordingOffsetMs = 1200
                    },
                    new HighlightEvent(PlatformType.Bilibili, "room-1", DateTimeOffset.UtcNow, 24.1, 2.8, "关键词集中")
                    {
                        RecordingSessionId = session.Id,
                        RecordingOffsetMs = 1900
                    }
                }
            };

            var viewModel = CreateViewModel(ffmpegPath, highlights);
            try
            {
                await viewModel.OpenSessionAsync(session);

                Assert.Equal(videoPath, viewModel.CurrentVideoPath);
                Assert.Equal(jsonlPath, viewModel.CurrentDanmakuPath);
                Assert.True(viewModel.HasDanmaku);
                Assert.Equal("弹幕 2 条", viewModel.DanmakuInfoText);
                Assert.Equal(2, viewModel.Highlights.Count);
                Assert.Equal(TimeSpan.FromMilliseconds(1200), viewModel.Highlights[0].Position);
                Assert.Equal(TimeSpan.FromMilliseconds(1900), viewModel.Highlights[1].Position);
                Assert.Equal(2, viewModel.HighlightMarkers.Count);
                Assert.Equal(TimeSpan.FromMilliseconds(1200), viewModel.HighlightMarkers[0].Position);
                Assert.Contains("已加载", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task TranscribeSelection_WithoutVideo_SetsFriendlyStatus_AndSkipsProvider()
    {
        var asr = new FakeAsrProvider();
        var viewModel = CreateViewModel("ffmpeg", asr: asr);
        try
        {
            await viewModel.Asr.TranscribeSelectionCommand.ExecuteAsync(null);

            Assert.Equal(0, asr.CallCount);
            Assert.False(viewModel.HasVideo);
            Assert.False(viewModel.Asr.IsRunning);
            Assert.Contains("请先打开本地视频", viewModel.Asr.StatusText);
            Assert.NotNull(viewModel.Asr.ErrorText);
            Assert.Contains("请先打开本地视频", viewModel.Asr.ErrorText);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    [Fact]
    public async Task TranscribeSelection_WithVideo_PopulatesPanel_AndSeekToLineUsesAbsoluteTime()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return; // 与 Media 测试一致：缺 ffmpeg 时跳过。
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "asr-selection.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var asr = new FakeAsrProvider
            {
                Handler = (_, start, end) => Task.FromResult(new AsrTranscriptResult(
                    start,
                    end,
                    "你好 世界",
                    new[]
                    {
                        new AsrTranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "你好"),
                        new AsrTranscriptSegment(TimeSpan.FromSeconds(1), end - start, "世界")
                    },
                    "fake (whisper-large-v3-turbo)",
                    DateTimeOffset.UtcNow))
            };

            var viewModel = CreateViewModel(ffmpegPath, asr: asr);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);

                var selectionStart = TimeSpan.FromMilliseconds(400);
                var selectionEnd = TimeSpan.FromMilliseconds(1400);
                viewModel.UpdateSelection(selectionStart, selectionEnd);

                await viewModel.Asr.TranscribeSelectionCommand.ExecuteAsync(null);

                Assert.Equal(1, asr.CallCount);
                Assert.Equal(videoPath, asr.Calls[0].VideoPath);
                Assert.Equal(selectionStart, asr.Calls[0].Start);
                Assert.Equal(selectionEnd, asr.Calls[0].End);
                Assert.Equal(2, viewModel.Asr.TranscriptLines.Count);
                Assert.Equal("你好 世界", viewModel.Asr.FullText);
                Assert.Equal("fake (whisper-large-v3-turbo)", viewModel.Asr.ProviderLabel);
                Assert.False(viewModel.Asr.IsRunning);
                Assert.Null(viewModel.Asr.ErrorText);
                Assert.Contains("识别完成", viewModel.Asr.StatusText);

                TimeSpan? sought = null;
                viewModel.SeekRequested += position => sought = position;
                viewModel.Asr.SeekToLine(viewModel.Asr.TranscriptLines[1]);

                Assert.Equal(selectionStart + TimeSpan.FromSeconds(1), sought);
                Assert.Equal(selectionStart + TimeSpan.FromSeconds(1), viewModel.Position);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task TranscribeSelection_WhenProviderThrows_ShowsFriendlyError_AndStopsRunning()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "asr-error.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            const string message = "尚未配置语音识别 API，请前往「账号与设置 → 语音识别」填写 API Key。";
            var asr = new FakeAsrProvider
            {
                Handler = (_, _, _) => Task.FromException<AsrTranscriptResult>(new InvalidOperationException(message))
            };

            var viewModel = CreateViewModel(ffmpegPath, asr: asr);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);
                viewModel.UpdateSelection(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1200));

                await viewModel.Asr.TranscribeSelectionCommand.ExecuteAsync(null);

                Assert.Equal(1, asr.CallCount);
                Assert.False(viewModel.Asr.IsRunning);
                Assert.Empty(viewModel.Asr.TranscriptLines);
                Assert.Contains("识别失败", viewModel.Asr.StatusText);
                Assert.NotNull(viewModel.Asr.ErrorText);
                Assert.Contains("尚未配置语音识别 API", viewModel.Asr.ErrorText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task TranscribeHighlight_UsesLeadTailRange_AndFillsCardResult()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "asr-highlight.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var asr = new FakeAsrProvider
            {
                Handler = (_, start, end) => Task.FromResult(new AsrTranscriptResult(
                    start,
                    end,
                    "高能片段回放",
                    new[] { new AsrTranscriptSegment(TimeSpan.Zero, end - start, "高能片段回放") },
                    "fake (whisper-large-v3-turbo)",
                    DateTimeOffset.UtcNow))
            };

            var viewModel = CreateViewModel(ffmpegPath, asr: asr);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);

                var item = new ClipHighlightItemViewModel(
                    new HighlightEvent(PlatformType.Bilibili, "room-1", DateTimeOffset.UtcNow, 18.75, 3.5, "弹幕速率突增"),
                    TimeSpan.FromMilliseconds(800));
                viewModel.Highlights.Add(item);

                await viewModel.Asr.TranscribeHighlightCommand.ExecuteAsync(item);

                Assert.Equal(1, asr.CallCount);
                Assert.Equal(TimeSpan.Zero, asr.Calls[0].Start);
                Assert.Equal(viewModel.Duration, asr.Calls[0].End);
                Assert.NotNull(item.AsrResult);
                Assert.True(item.HasAsrResult);
                Assert.False(item.IsTranscribing);
                Assert.Equal("高能片段回放", item.AsrSummary);
                Assert.Single(viewModel.Asr.TranscriptLines);
                Assert.Contains("识别完成", viewModel.Asr.StatusText);

                // 已有结果时再次点击卡片按钮只展示结果，不重复调用 Provider（Provider 缓存之外再省一次抽音）。
                await viewModel.Asr.TranscribeHighlightCommand.ExecuteAsync(item);
                Assert.Equal(1, asr.CallCount);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task UpdateSelection_NotifiesAsrAvailability()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "asr-availability.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var viewModel = CreateViewModel(ffmpegPath);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);
                Assert.True(viewModel.Asr.CanTranscribeSelection);

                var changes = new List<string>();
                viewModel.Asr.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is not null)
                    {
                        changes.Add(e.PropertyName);
                    }
                };

                viewModel.UpdateSelection(
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(1400));

                Assert.Contains(nameof(ClipAsrViewModel.CanTranscribeSelection), changes);
                Assert.True(viewModel.Asr.CanTranscribeSelection);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task OpenLocalFile_ResetsAsrPanel()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "asr-reset.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var asr = new FakeAsrProvider
            {
                Handler = (_, start, end) => Task.FromResult(new AsrTranscriptResult(
                    start,
                    end,
                    "重置前文本",
                    new[] { new AsrTranscriptSegment(TimeSpan.Zero, end - start, "重置前文本") },
                    "fake",
                    DateTimeOffset.UtcNow))
            };

            var viewModel = CreateViewModel(ffmpegPath, asr: asr);
            try
            {
                viewModel.PickVideoFileAsync = () => Task.FromResult<string?>(videoPath);
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);
                viewModel.UpdateSelection(
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(1400));
                await viewModel.Asr.TranscribeSelectionCommand.ExecuteAsync(null);

                Assert.True(viewModel.Asr.HasResult);
                Assert.Equal("重置前文本", viewModel.Asr.FullText);
                Assert.Single(viewModel.Asr.TranscriptLines);

                // 重新打开本地视频：ASR 面板与卡片状态由主视图模型统一重置。
                await viewModel.OpenLocalFileCommand.ExecuteAsync(null);

                Assert.False(viewModel.Asr.HasResult);
                Assert.Equal("尚未识别", viewModel.Asr.StatusText);
                Assert.Empty(viewModel.Asr.TranscriptLines);
                Assert.Equal(string.Empty, viewModel.Asr.FullText);
                Assert.Equal(string.Empty, viewModel.Asr.ProviderLabel);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 边录边剪：场次已在录制中时改为读取分片文件进入 live 模式，时长/安全截止来自实时进度。
    /// </summary>
    [Fact]
    public async Task OpenSession_LiveSession_EntersLiveMode_AndLoadsPartFile()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor);
            try
            {
                string? loadedPath = null;
                viewModel.LoadLiveVideoRequested += path => loadedPath = path;

                await viewModel.OpenSessionAsync(session);

                Assert.True(viewModel.IsLiveSession);
                Assert.Equal(TimeSpan.FromSeconds(12), viewModel.LiveRecordedDuration);
                Assert.Equal(TimeSpan.FromSeconds(12), viewModel.Duration);
                Assert.Equal(TimeSpan.FromSeconds(7), viewModel.SafeClipDuration);
                Assert.Equal(partPath, viewModel.CurrentVideoPath);
                Assert.Equal(partPath, loadedPath);
                Assert.Contains("正在录制中", viewModel.StatusText);
                Assert.Contains("正在录制", viewModel.LiveStatusText);
                Assert.Equal(TimeSpan.Zero, viewModel.InPoint);
                Assert.Equal(TimeSpan.FromSeconds(7), viewModel.OutPoint);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：录制已发生自动分段（第 2 段）时不允许 realtime 剪辑，只给出中文提示。</summary>
    [Fact]
    public async Task OpenSession_LiveSessionWithSecondPart_ReportsAutoSplit_AndStaysOffline()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part002.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo with { PartNumber = 2 } };

            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor);
            try
            {
                var loadRequested = false;
                viewModel.LoadLiveVideoRequested += _ => loadRequested = true;

                await viewModel.OpenSessionAsync(session);

                Assert.False(viewModel.IsLiveSession);
                Assert.False(loadRequested);
                Assert.Contains("自动分段", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：选区与导出终点都必须钳制在安全可剪截止（已录 - 5 秒）以内。</summary>
    [Fact]
    public async Task LiveSession_ClampsSelectionAndExportToSafeDuration()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var exporter = new FakeClipExporter();
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor, exporter: exporter);
            try
            {
                await viewModel.OpenSessionAsync(session);
                viewModel.Export.OutputDirectory = tempDirectory;
                viewModel.Export.OutputFileName = "live-clip.mp4";

                // 用户把出点拖到已录尾部：live 模式下先被选区钳制到安全截止。
                viewModel.UpdateSelection(TimeSpan.Zero, TimeSpan.FromSeconds(11));
                Assert.Equal(TimeSpan.FromSeconds(7), viewModel.OutPoint);

                await viewModel.Export.ExportCommand.ExecuteAsync(null);

                var request = Assert.Single(exporter.Requests);
                Assert.Equal(TimeSpan.Zero, request.StartTime);
                Assert.Equal(TimeSpan.FromSeconds(7), request.EndTime);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：手动同步只增量读取新弹幕，条数随时间增长。</summary>
    [Fact]
    public async Task LiveSession_SyncProgress_AppendsIncrementalDanmaku()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var jsonlPath = Path.Combine(tempDirectory, "live.jsonl");
            await File.WriteAllLinesAsync(jsonlPath, new[]
            {
                "{\"type\":\"meta\",\"version\":1}",
                "{\"type\":\"dm\",\"offsetMs\":500,\"uname\":\"甲\",\"text\":\"第一条\"}",
                "{\"type\":\"dm\",\"offsetMs\":1500,\"uname\":\"乙\",\"text\":\"第二条\"}"
            });

            var session = CreateLiveSession(partPath, out var liveInfo, jsonlPath);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor);
            try
            {
                await viewModel.OpenSessionAsync(session);
                Assert.Equal(2, viewModel.DanmakuItemCount);

                await File.AppendAllTextAsync(
                    jsonlPath,
                    "{\"type\":\"dm\",\"offsetMs\":2500,\"uname\":\"丙\",\"text\":\"第三条\"}\n");
                monitor.Live = liveInfo with { DanmakuCount = 3 };

                await viewModel.SyncLiveProgressCommand.ExecuteAsync(null);

                Assert.Equal(3, viewModel.DanmakuItemCount);
                Assert.Contains("弹幕 3 条", viewModel.DanmakuInfoText);
                Assert.Contains("已同步最新录制进度", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：波形增量提取必须落在 0.5s 桶网格上，拼接后与时间轴桶一一对应（无相位漂移）。</summary>
    [Fact]
    public async Task LiveSession_SyncProgress_AlignsWaveformExtractionToBucketGrid()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var waveforms = new FakeLiveWaveformExtractor
            {
                Handler = (from, to) =>
                {
                    var count = Math.Max(0, (int)Math.Round((to - from).TotalSeconds * 2));
                    return Enumerable.Repeat(0.5f, count).ToArray();
                }
            };
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor, liveWaveforms: waveforms);
            try
            {
                await viewModel.OpenSessionAsync(session);

                // 已录 12s → 安全 7s：首次提取 [0, 7s]，共 14 个 0.5s 桶。
                Assert.Equal(
                    (TimeSpan.Zero, TimeSpan.FromSeconds(7)),
                    waveforms.Calls[^1]);

                // 已录增长到 12.6s（安全 7.6s）：提取终点应对齐到 7.5s 而不是 7.6s，避免半桶漂移。
                monitor.Live = liveInfo with { RecordedDuration = TimeSpan.FromSeconds(12.6) };
                await viewModel.SyncLiveProgressCommand.ExecuteAsync(null);

                Assert.Equal(
                    (TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.5)),
                    waveforms.Calls[^1]);

                var waveform = viewModel.Waveform;
                Assert.NotNull(waveform);
                Assert.Equal(26, waveform!.AudioPeaks.Length); // ceil(12.6 / 0.5)
                Assert.Equal(15, waveform.AudioPeaks.Count(peak => peak > 0.4f)); // 7.5s / 0.5s 个有效桶
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：同步时发现录制已切到后续分片，必须停止 live 模式并提示等待录制结束。</summary>
    [Fact]
    public async Task LiveSession_SyncProgressWhenPartRolled_StopsLiveMode()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor);
            try
            {
                await viewModel.OpenSessionAsync(session);
                Assert.True(viewModel.IsLiveSession);

                monitor.Live = liveInfo with
                {
                    PartNumber = 2,
                    PartFilePath = Path.Combine(tempDirectory, "live.part002.mkv"),
                    RecordedDuration = TimeSpan.FromSeconds(20)
                };

                await viewModel.SyncLiveProgressCommand.ExecuteAsync(null);

                Assert.False(viewModel.IsLiveSession);
                Assert.Contains("自动分段", viewModel.StatusText);
                Assert.False(viewModel.SyncLiveProgressCommand.CanExecute(null));
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：已录不足 5 秒（安全缓冲尚未成型）时拒绝导出，避免读到半截写入的数据。</summary>
    [Fact]
    public async Task LiveSession_ExportBlockedWhileSafetyBufferMissing()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor
            {
                Live = liveInfo with { RecordedDuration = TimeSpan.FromSeconds(4) }
            };
            var exporter = new FakeClipExporter();
            var viewModel = CreateViewModel(FfmpegPathOrFallback(), live: monitor, exporter: exporter);
            try
            {
                await viewModel.OpenSessionAsync(session);
                Assert.True(viewModel.IsLiveSession);
                Assert.Equal(TimeSpan.Zero, viewModel.SafeClipDuration);

                viewModel.Export.OutputDirectory = tempDirectory;
                viewModel.Export.OutputFileName = "too-early.mp4";
                await viewModel.Export.ExportCommand.ExecuteAsync(null);

                Assert.Empty(exporter.Requests);
                Assert.Contains("安全缓冲生成中", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>边录边剪：录制结束后平滑切到最终文件的离线剪辑模式。</summary>
    [Fact]
    public async Task OnLiveSessionCompleted_SwitchesToOfflineMode()
    {
        if (!FfmpegLocator.TryLocate(out var ffmpegPath))
        {
            return;
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var videoPath = Path.Combine(tempDirectory, "final.mp4");
            GenerateVideo(ffmpegPath, videoPath);

            var partPath = Path.Combine(tempDirectory, "live.part001.mkv");
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>());

            var session = CreateLiveSession(partPath, out var liveInfo);
            var monitor = new FakeLiveRecordingMonitor { Live = liveInfo };
            var sessions = new FakeRecordingSessionRepository();
            var viewModel = CreateViewModel(ffmpegPath, live: monitor, sessions: sessions);
            try
            {
                await viewModel.OpenSessionAsync(session);
                Assert.True(viewModel.IsLiveSession);

                // 录制结束：最终文件落盘，仓储可查到同一场次。
                session.VideoFilePath = videoPath;
                session.Status = RecordingStatus.Completed;
                sessions.Session = session;
                monitor.Live = null;

                await viewModel.OnLiveSessionCompletedAsync(session);

                Assert.False(viewModel.IsLiveSession);
                Assert.Equal(TimeSpan.Zero, viewModel.SafeClipDuration);
                Assert.Equal(videoPath, viewModel.CurrentVideoPath);
                Assert.Contains("已切换为离线剪辑模式", viewModel.StatusText);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static RecordingSession CreateLiveSession(
        string partPath,
        out LiveRecordingInfo liveInfo,
        string? danmakuFilePath = null)
    {
        var session = new RecordingSession
        {
            Platform = PlatformType.Bilibili,
            RoomId = "room-live",
            RoomDisplayName = "直播中房间",
            StartedAt = DateTime.UtcNow,
            Status = RecordingStatus.Recording,
            DanmakuFilePath = danmakuFilePath ?? string.Empty
        };

        liveInfo = new LiveRecordingInfo(
            session.Id,
            Guid.NewGuid(),
            1,
            "直播中房间",
            partPath,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12),
            1024 * 1024,
            2,
            1);
        return session;
    }

    private static string FfmpegPathOrFallback() =>
        FfmpegLocator.TryLocate(out var path) ? path : "ffmpeg";

    private static ClipStudioViewModel CreateViewModel(
        string ffmpegPath,
        FakeHighlightEventRepository? highlights = null,
        FakeAsrProvider? asr = null,
        FakeRecordingSessionRepository? sessions = null,
        FakeLiveRecordingMonitor? live = null,
        FakeLiveWaveformExtractor? liveWaveforms = null,
        FakeClipExporter? exporter = null)
    {
        return new ClipStudioViewModel(
            sessions ?? new FakeRecordingSessionRepository(),
            highlights ?? new FakeHighlightEventRepository(),
            new FakeAppSettingsRepository(),
            new VideoProbeService(ffmpegPath),
            new FakeWaveformService(),
            new FakeHardwareEncoderDetector(),
            exporter ?? new FakeClipExporter(),
            live ?? new FakeLiveRecordingMonitor(),
            liveWaveforms ?? new FakeLiveWaveformExtractor(),
            asr ?? new FakeAsrProvider());
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "livemonitor-clipvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void GenerateVideo(string ffmpegPath, string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("testsrc=duration=2:size=160x120:rate=10");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("mpeg4");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(outputPath), $"生成测试视频失败：{stderr}");
    }

    private sealed class FakeRecordingSessionRepository : IRecordingSessionRepository
    {
        /// <summary>GetByIdAsync 返回的场次；null 表示查无此场次。</summary>
        public RecordingSession? Session { get; set; }

        public Task AddAsync(RecordingSession session, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(RecordingSession session, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Session is not null && Session.Id == id ? Session : null);

        public Task<IReadOnlyList<RecordingSession>> GetRecentAsync(int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());

        public Task<IReadOnlyList<RecordingSession>> GetByRoomAsync(
            PlatformType platform,
            string roomId,
            int limit = 100,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());

        public Task<IReadOnlyList<RecordingSession>> GetUnfinishedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHighlightEventRepository : IHighlightEventRepository
    {
        public List<HighlightEvent> Events { get; } = new();

        public Task AddAsync(HighlightEvent highlightEvent, CancellationToken ct = default)
        {
            Events.Add(highlightEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HighlightEvent>> GetRecentAsync(int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HighlightEvent>>(Events.Take(limit).ToArray());

        public Task<IReadOnlyList<HighlightEvent>> GetByRecordingSessionAsync(
            Guid sessionId,
            int limit = 500,
            CancellationToken ct = default)
        {
            var result = Events
                .Where(item => item.RecordingSessionId == sessionId)
                .OrderBy(item => item.RecordingOffsetMs ?? 0)
                .Take(limit)
                .ToArray();
            return Task.FromResult<IReadOnlyList<HighlightEvent>>(result);
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Events.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWaveformService : IWaveformService
    {
        public Task<WaveformData> GetWaveformAsync(
            string videoPath,
            TimeSpan duration,
            string? companionDanmakuPath = null,
            int bucketCount = 2000,
            CancellationToken ct = default) =>
            Task.FromResult(new WaveformData(
                duration,
                new float[Math.Min(bucketCount, 16)],
                new float[Math.Min(bucketCount, 16)],
                0));
    }

    private sealed class FakeHardwareEncoderDetector : IHardwareEncoderDetector
    {
        public Task<(string EncoderName, string[] ExtraArgs)> DetectBestAsync(CancellationToken ct = default) =>
            Task.FromResult(("libx264", Array.Empty<string>()));
    }

    private sealed class FakeClipExporter : IClipExporter
    {
        public List<ClipExportRequest> Requests { get; } = new();

        public Task<IReadOnlyList<string>> ExportAsync(
            ClipExportRequest request,
            IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
            string sourceVideoCodec,
            IProgress<ClipExportProgress>? progress = null,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    /// <summary>可配置“正在录制”场次并手动触发录制结束事件的监控器 Fake。</summary>
    private sealed class FakeLiveRecordingMonitor : ILiveRecordingMonitor
    {
        public LiveRecordingInfo? Live { get; set; }

        public IReadOnlyList<LiveRecordingInfo> GetLiveRecordings() =>
            Live is null ? Array.Empty<LiveRecordingInfo>() : new[] { Live };

        public bool TryGetLiveRecording(Guid sessionId, out LiveRecordingInfo info)
        {
            if (Live is not null && Live.SessionId == sessionId)
            {
                info = Live;
                return true;
            }

            info = null!;
            return false;
        }

        public event Action<RecordingSession>? SessionCompleted;

        public void RaiseSessionCompleted(RecordingSession session) => SessionCompleted?.Invoke(session);
    }

    /// <summary>不启动 ffmpeg 的增量波形 Fake：记录调用区间并返回可配置峰值。</summary>
    private sealed class FakeLiveWaveformExtractor : ILiveWaveformExtractor
    {
        public List<(TimeSpan From, TimeSpan To)> Calls { get; } = new();

        public Func<TimeSpan, TimeSpan, float[]>? Handler { get; set; }

        public Task<float[]> ExtractPeaksAsync(
            string videoPath,
            TimeSpan fromTime,
            TimeSpan toTime,
            int peaksPerSecond = 2,
            CancellationToken ct = default)
        {
            Calls.Add((fromTime, toTime));
            return Task.FromResult(Handler?.Invoke(fromTime, toTime) ?? Array.Empty<float>());
        }
    }

    private sealed record AsrCall(string VideoPath, TimeSpan Start, TimeSpan End);

    /// <summary>可配置返回值/异常并记录调用参数的 ASR Fake：测试不发起任何真实网络请求。</summary>
    private sealed class FakeAsrProvider : IAsrProvider
    {
        public List<AsrCall> Calls { get; } = new();

        public int CallCount => Calls.Count;

        public Func<string, TimeSpan, TimeSpan, Task<AsrTranscriptResult>>? Handler { get; set; }

        public Task<AsrTranscriptResult> TranscribeSegmentAsync(
            string videoPath,
            TimeSpan startTime,
            TimeSpan endTime,
            CancellationToken ct = default)
        {
            Calls.Add(new AsrCall(videoPath, startTime, endTime));
            if (Handler is not null)
            {
                return Handler(videoPath, startTime, endTime);
            }

            return Task.FromResult(new AsrTranscriptResult(
                startTime,
                endTime,
                string.Empty,
                Array.Empty<AsrTranscriptSegment>(),
                "fake",
                DateTimeOffset.UtcNow));
        }
    }
}
