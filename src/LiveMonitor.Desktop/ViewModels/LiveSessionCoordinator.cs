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
    private readonly SemaphoreSlim _waveformGate = new(1, 1);

    private bool _isSyncing;
    private bool _disposed;

    public LiveSessionCoordinator(
        LiveClipSession session,
        ILiveRecordingMonitor recordings,
        ILiveWaveformExtractor waveforms,
        IIncrementalDanmakuReader danmakuReader,
        ILiveSessionTimer timer,
        ILiveSessionSyncHost host)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _danmakuReader = danmakuReader ?? throw new ArgumentNullException(nameof(danmakuReader));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _host = host ?? throw new ArgumentNullException(nameof(host));
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
