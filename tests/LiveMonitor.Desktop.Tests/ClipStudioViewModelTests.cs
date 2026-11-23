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
            await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

            Assert.Equal(0, asr.CallCount);
            Assert.False(viewModel.HasVideo);
            Assert.False(viewModel.IsAsrRunning);
            Assert.Contains("请先打开本地视频", viewModel.AsrStatusText);
            Assert.NotNull(viewModel.AsrErrorText);
            Assert.Contains("请先打开本地视频", viewModel.AsrErrorText);
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

                await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

                Assert.Equal(1, asr.CallCount);
                Assert.Equal(videoPath, asr.Calls[0].VideoPath);
                Assert.Equal(selectionStart, asr.Calls[0].Start);
                Assert.Equal(selectionEnd, asr.Calls[0].End);
                Assert.Equal(2, viewModel.AsrLines.Count);
                Assert.Equal("你好 世界", viewModel.AsrFullText);
                Assert.Equal("fake (whisper-large-v3-turbo)", viewModel.AsrProviderLabel);
                Assert.False(viewModel.IsAsrRunning);
                Assert.Null(viewModel.AsrErrorText);
                Assert.Contains("识别完成", viewModel.AsrStatusText);

                TimeSpan? sought = null;
                viewModel.SeekRequested += position => sought = position;
                viewModel.SeekToAsrLine(viewModel.AsrLines[1]);

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

                await viewModel.TranscribeSelectionCommand.ExecuteAsync(null);

                Assert.Equal(1, asr.CallCount);
                Assert.False(viewModel.IsAsrRunning);
                Assert.Empty(viewModel.AsrLines);
                Assert.Contains("识别失败", viewModel.AsrStatusText);
                Assert.NotNull(viewModel.AsrErrorText);
                Assert.Contains("尚未配置语音识别 API", viewModel.AsrErrorText);
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

                await viewModel.TranscribeHighlightCommand.ExecuteAsync(item);

                Assert.Equal(1, asr.CallCount);
                Assert.Equal(TimeSpan.Zero, asr.Calls[0].Start);
                Assert.Equal(viewModel.Duration, asr.Calls[0].End);
                Assert.NotNull(item.AsrResult);
                Assert.True(item.HasAsrResult);
                Assert.False(item.IsTranscribing);
                Assert.Equal("高能片段回放", item.AsrSummary);
                Assert.Single(viewModel.AsrLines);
                Assert.Contains("识别完成", viewModel.AsrStatusText);

                // 已有结果时再次点击卡片按钮只展示结果，不重复调用 Provider（Provider 缓存之外再省一次抽音）。
                await viewModel.TranscribeHighlightCommand.ExecuteAsync(item);
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

    private static ClipStudioViewModel CreateViewModel(
        string ffmpegPath,
        FakeHighlightEventRepository? highlights = null,
        FakeAsrProvider? asr = null)
    {
        return new ClipStudioViewModel(
            new FakeRecordingSessionRepository(),
            highlights ?? new FakeHighlightEventRepository(),
            new FakeAppSettingsRepository(),
            new VideoProbeService(ffmpegPath),
            new FakeWaveformService(),
            new FakeHardwareEncoderDetector(),
            new FakeClipExporter(),
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
        public Task AddAsync(RecordingSession session, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(RecordingSession session, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<RecordingSession?>(null);

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
        public Task<IReadOnlyList<string>> ExportAsync(
            ClipExportRequest request,
            IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
            string sourceVideoCodec,
            IProgress<ClipExportProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
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
