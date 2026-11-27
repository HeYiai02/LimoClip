using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Controls;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 「边录边剪」同步编排：10 秒自动同步 / 手动同步、增量弹幕、增量波形与状态推进。
/// 数据（<see cref="LiveClipSession"/>）与 UI 通知仍由 <see cref="ClipStudioViewModel"/> 持有，
/// 本类只负责顺序与策略，通过 <see cref="ILiveSessionSyncHost"/> 显式回调视图模型，
/// 从而可以在无 Avalonia 的单元测试里断言「先进度、再弹幕/高能、最后波形/重载」的固定顺序。
/// </summary>
internal sealed class LiveSessionCoordinator : IDisposable
{
    /// <summary>自动同步间隔：只刷新进度/弹幕/高能/密度，不重载播放器、不打断当前播放。</summary>
    public static readonly TimeSpan AutoSyncInterval = TimeSpan.FromSeconds(10);

    private readonly LiveClipSession _session;
    private readonly ILiveRecordingMonitor _recordings;
    private readonly ILiveWaveformExtractor _waveforms;
    private readonly IIncrementalDanmakuReader _danmakuReader;
    private readonly ILiveSessionTimer _timer;
    private readonly ILiveSessionSyncHost _host;
    private readonly ILiveSessionOpenHost? _openHost;
    private readonly ILiveSessionCompletionHost? _completionHost;
    private readonly SemaphoreSlim _waveformGate = new(1, 1);

    private bool _isSyncing;
    private bool _disposed;

    public LiveSessionCoordinator(
        LiveClipSession session,
        ILiveRecordingMonitor recordings,
        ILiveWaveformExtractor waveforms,
        IIncrementalDanmakuReader danmakuReader,
        ILiveSessionTimer timer,
        ILiveSessionSyncHost host,
        ILiveSessionOpenHost? openHost = null,
        ILiveSessionCompletionHost? completionHost = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _danmakuReader = danmakuReader ?? throw new ArgumentNullException(nameof(danmakuReader));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _openHost = openHost;
        _completionHost = completionHost;
    }

    /// <summary>是否正在执行同步（防手动/自动同步重入）。</summary>
    public bool IsSyncing => _isSyncing;

    /// <summary>进入 live 模式后启动 10 秒自动同步（会话未激活时忽略）。</summary>
    public void StartAutoSync()
    {
        if (_disposed || !_session.IsActive)
        {
            return;
        }

        _timer.Start(AutoSyncInterval, AutoSyncAsync);
    }

    /// <summary>停止自动同步；退出 live 模式、录制结束与释放时调用。</summary>
    public void StopAutoSync() => _timer.Stop();

    /// <summary>
    /// 打开「录制中」的场次：分段拒绝、分片存在性、分片探测（失败按实时进度兜底）、
    /// 进入 live 模式、弹幕/高亮点亮与打开收尾；每一步之后都检查是否已被更新的加载取代。
    /// </summary>
    public async Task OpenAsync(RecordingSession session, LiveRecordingInfo liveInfo)
    {
        var host = _openHost ?? throw new InvalidOperationException("未配置 live 会话打开宿主。");
        if (_disposed)
        {
            return;
        }

        if (liveInfo.PartNumber > 1)
        {
            host.SetStatus(ClipStudioStatusText.LiveSegmented(liveInfo.PartNumber));
            return;
        }

        var handle = host.BeginLoad();
        host.SetBusy(true);
        host.SetStatus(ClipStudioStatusText.LiveOpening);
        try
        {
            var partPath = liveInfo.PartFilePath;
            if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
            {
                host.SetStatus(ClipStudioStatusText.LivePartMissing(partPath));
                return;
            }

            var danmakuPath = ResolveDanmakuPath(session.DanmakuFilePath);
            host.PrepareLiveSessionFields(session, partPath, danmakuPath);
            host.SetLiveVideoTitle(ClipStudioPresentationLogic.BuildLiveVideoTitle(session));

            // 录制中的分片时长通常是 N/A：探测失败是预期路径，用实时进度兜底。
            VideoMetadata? metadata = null;
            try
            {
                metadata = await host.ProbeVideoAsync(partPath, handle.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 分片尚未写完索引 / duration 为 N/A，忽略并按实时进度继续。
            }

            if (!host.IsLoadCurrent(handle))
            {
                return;
            }

            host.EnterLiveSession(session, liveInfo, partPath, metadata);
            if (danmakuPath is not null)
            {
                await RefreshDanmakuAsync(handle.Token).ConfigureAwait(true);
                if (!host.IsLoadCurrent(handle))
                {
                    return;
                }
            }

            await host.LoadHighlightsAsync(session, _session.RecordedDuration, handle.Token)
                .ConfigureAwait(true);
            if (!host.IsLoadCurrent(handle))
            {
                return;
            }

            host.CompleteLiveOpen(partPath);
            StartAutoSync();
            _ = ExtractWaveformAsync(handle.WaveformToken);
        }
        catch (OperationCanceledException)
        {
            // 切换场次时取消上一次加载，静默忽略。
        }
        catch (Exception ex)
        {
            if (host.IsLoadCurrent(handle))
            {
                host.SetStatus(ClipStudioStatusText.LiveOpenFailed(ex.Message));
            }
        }
        finally
        {
            if (host.IsLoadCurrent(handle))
            {
                host.SetBusy(false);
            }
        }
    }

    /// <summary>
    /// 录制结束：停止自动同步、记录结束瞬间的位置与选区、切回离线模式，再把最终文件、弹幕、
    /// 高光列表与场次列表刷新到位；缺文件与异常分别落到明确的失败文案。
    /// </summary>
    public async Task CompleteAsync(RecordingSession session, CancellationToken ct = default)
    {
        if (session is null || _disposed)
        {
            return;
        }

        var host = _completionHost ?? throw new InvalidOperationException("未配置 live 会话结束宿主。");
        if (!_session.IsActive || host.CurrentSessionId != session.Id)
        {
            return;
        }

        StopAutoSync();
        var lastPosition = host.LastPosition;
        var lastIn = host.LastInPoint;
        var lastOut = host.LastOutPoint;
        host.ExitLiveMode();

        try
        {
            var latest = await host.LoadSessionAsync(ct).ConfigureAwait(true);
            if (latest is null || string.IsNullOrWhiteSpace(latest.VideoFilePath) || !File.Exists(latest.VideoFilePath))
            {
                host.SetStatus(ClipStudioStatusText.LiveCompletedWithoutFile(latest?.ErrorMessage));
                return;
            }

            if (host.IsDisposed)
            {
                return;
            }

            var danmakuPath = ResolveDanmakuPath(latest.DanmakuFilePath);
            await host.OpenOfflineAsync(latest.VideoFilePath, danmakuPath, latest, ct).ConfigureAwait(true);
            if (host.IsDisposed)
            {
                return;
            }

            // 录制已结束：刷新列表，让该场次从「录制中」切换为普通历史项。
            await host.RefreshSessionsAsync(ct).ConfigureAwait(true);
            if (host.IsDisposed)
            {
                return;
            }

            if (lastOut > TimeSpan.Zero && lastOut > lastIn + ClipSelectionLogic.MinSelection)
            {
                host.RestoreSelection(lastIn, lastOut);
            }

            host.SeekTo(lastPosition);
            host.RaiseTimelineChanged();
            host.SetStatus(ClipStudioStatusText.LiveCompleted);
        }
        catch (OperationCanceledException)
        {
            // 视图模型释放或用户离开剪辑台：静默放弃切离线。
        }
        catch (Exception ex)
        {
            host.SetStatus(ClipStudioStatusText.LiveSwitchToOfflineFailed(ex.Message));
        }
    }

    /// <summary>弹幕文件存在才纳入边录边剪（录制刚开始时 jsonl 可能尚未创建）。</summary>
    internal static string? ResolveDanmakuPath(string? danmakuFilePath) =>
        !string.IsNullOrWhiteSpace(danmakuFilePath) && File.Exists(danmakuFilePath)
            ? danmakuFilePath
            : null;

    /// <summary>手动同步（按钮 / F5）：含波形提取，并在完成后重载播放器尾部数据。</summary>
    public async Task SyncManualAsync()
    {
        if (_disposed || !_session.IsActive || _isSyncing)
        {
            return;
        }

        SetSyncing(true);
        try
        {
            await SyncCoreAsync(includeAudioPeaks: true, requestPlayerReload: true, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.SetStatus(ClipStudioStatusText.LiveSyncFailed(ex.Message));
        }
        finally
        {
            SetSyncing(false);
        }
    }

    /// <summary>10 秒自动同步：只刷新进度/弹幕/高能/密度，不重载播放器、不打断当前播放。</summary>
    public async Task AutoSyncAsync()
    {
        if (_disposed || !_session.IsActive || _isSyncing || !_host.IsAutoSyncEnabled)
        {
            return;
        }

        SetSyncing(true);
        try
        {
            await SyncCoreAsync(includeAudioPeaks: false, requestPlayerReload: false, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.SetStatus(ClipStudioStatusText.AutoSyncFailed(ex.Message));
        }
        finally
        {
            SetSyncing(false);
        }
    }

    /// <summary>增量读取新弹幕（按 byte offset），并同步追加到回放游标。</summary>
    public async Task RefreshDanmakuAsync(CancellationToken ct)
    {
        if (_host.DanmakuPath is not { } danmakuPath)
        {
            return;
        }

        try
        {
            var result = await _danmakuReader
                .ReadIncrementalAsync(danmakuPath, _session.DanmakuOffset, ct)
                .ConfigureAwait(true);
            _session.AdviseDanmakuOffset(result.NextByteOffset);
            if (result.NewItems.Count == 0)
            {
                return;
            }

            _host.ApplyDanmaku(result.NewItems);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            // 录制刚开始时 jsonl 可能尚未创建，忽略即可。
        }
        catch (Exception ex)
        {
            _host.SetStatus(ClipStudioStatusText.LiveDanmakuLoadFailed(ex.Message));
        }
    }

    /// <summary>边录边剪音频波形：只提取上次截止点之后新增的安全区段，追加到固定分辨率峰值数组。</summary>
    public async Task ExtractWaveformAsync(CancellationToken ct)
    {
        // 初始加载与手动同步可能同时触发提取：串行化，避免重复追加同一段峰值。
        var entered = false;
        try
        {
            await _waveformGate.WaitAsync(ct).ConfigureAwait(true);
            entered = true;

            if (ct.IsCancellationRequested || _host.IsDisposed || !_session.IsActive || _session.PartPath is null)
            {
                return;
            }

            // 提取终点对齐到 0.5s 桶网格：每段返回的峰值数严格等于时间轴桶数，
            // 直接首尾拼接才不会出现亚 0.5s 的相位漂移。
            if (!_session.TryTakeWaveformWindow(out var window))
            {
                return;
            }

            var from = _session.PeaksExtractedUntil;
            var to = window.To;
            var peaks = await _waveforms
                .ExtractPeaksAsync(_session.PartPath, from, to, LiveWaveformLogic.PeaksPerSecond, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested || _host.IsDisposed || !_session.IsActive)
            {
                return;
            }

            _session.AppendPeaks(peaks, window.ExpectedCount);
            _session.MarkPeaksExtracted(to);
            _host.ApplyWaveform();
            _host.RaiseTimelineChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // 分片暂无可解码音频（录制刚开始 / 无音轨）或窗口销毁时信号量已释放：
            // 按“本次无数据”降级，等下次同步再试。
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception ex)
        {
            _host.SetStatus(ClipStudioStatusText.LiveWaveformFailed(ex.Message));
        }
        finally
        {
            if (entered)
            {
                _waveformGate.Release();
            }

            if (!ct.IsCancellationRequested)
            {
                _host.SetWaveformLoading(false);
            }
        }
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
            _timer.Stop();
        }
        catch (Exception)
        {
            // 释放路径忽略。
        }

        _waveformGate.Dispose();
    }

    private async Task SyncCoreAsync(bool includeAudioPeaks, bool requestPlayerReload, CancellationToken ct)
    {
        if (!_session.IsActive || _host.CurrentSession is null)
        {
            return;
        }

        if (!_recordings.TryGetLiveRecording(_session.Info!.SessionId, out var latest))
        {
            // 同步时发现录制已结束：直接切离线模式。
            await _host.HandleRecordingCompletedAsync().ConfigureAwait(true);
            return;
        }

        // 录制已进入后续分片：live 源仍是 part001，而时间轴 0 点是 part001 起点，
        // 继续同步会让画面/波形与时间轴错位；直接停止边录边剪并提示等录制结束。
        if (latest.PartNumber > 1)
        {
            _host.StopForSegmentedRecording(latest.PartNumber);
            return;
        }

        _host.ApplyProgress(latest);
        await RefreshDanmakuAsync(ct).ConfigureAwait(true);

        // 高能没有增量查询接口：每次全量拉最近 500 条重建，避免重复标记。
        await _host.ReloadHighlightsAsync(_host.LiveRecordedDuration, ct).ConfigureAwait(true);

        if (includeAudioPeaks)
        {
            await ExtractWaveformAsync(ct).ConfigureAwait(true);
        }

        _host.ApplyWaveform();
        _host.RaiseTimelineChanged();
        if (requestPlayerReload)
        {
            _host.RequestPlayerReload();
        }

        _host.SetStatus(ClipStudioStatusText.LiveSyncSucceeded(_host.LiveRecordedDuration, _host.SafeClipDuration));
    }

    private void SetSyncing(bool syncing)
    {
        _isSyncing = syncing;
        _host.SetSyncing(syncing);
    }
}
