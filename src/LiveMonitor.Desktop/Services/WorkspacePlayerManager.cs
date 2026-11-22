using Avalonia.Threading;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 按窗格管理 MpvPlayerView 实例。窗格从 Dock 树上摘下时播放器不销毁，
/// 只有关闭方案或显式移除时才 Dispose，避免拖拽换位把正在播放的流杀掉。
/// </summary>
public sealed class WorkspacePlayerManager : IDisposable
{
    private sealed record DropBaseline(long Drops, DateTimeOffset SampledAt, int BadCycles);

    private readonly AppServices _services;
    private readonly Dictionary<string, CancellationTokenSource> _starts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _offlinePolls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _qualityDebounces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DropBaseline> _dropBaselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _retryCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _retryDelays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MpvPlayerView> _playerByPane = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _parseGate = new(4, 4);
    private readonly DispatcherTimer _dropTimer;
    private readonly object _gate = new();
    private bool _disposed;

    public WorkspacePlayerManager(AppServices services)
    {
        _services = services;
        _dropTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _dropTimer.Tick += (_, _) => InspectDrops();
        _dropTimer.Start();
    }

    public MpvPlayerView EnsurePlayer(RoomTileViewModel tile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tile.PlayerView is not null)
        {
            _playerByPane[tile.PaneId] = tile.PlayerView;
            return tile.PlayerView;
        }

        var player = new MpvPlayerView();
        WirePlayer(tile, player);
        tile.PlayerView = player;
        _playerByPane[tile.PaneId] = player;
        return player;
    }

    /// <summary>
    /// 方案切换时把旧窗格的播放器 context 交给新窗格的新播放器控件，
    /// 旧控件立即摘下，新控件挂载后直接用同一路流继续渲染。
    /// </summary>
    public bool TransferPlayer(RoomTileViewModel oldTile, RoomTileViewModel newTile)
    {
        if (oldTile.PlayerView is not { } oldPlayer)
        {
            return false;
        }

        var context = oldPlayer.TakeContext();
        if (context is null)
        {
            return false;
        }

        var newPlayer = new MpvPlayerView();
        WirePlayer(newTile, newPlayer);
        newPlayer.AdoptContext(context, oldPlayer.CurrentQuality, oldPlayer.State, oldTile.DetailText);
        oldTile.PlayerView = null;
        _playerByPane.Remove(oldTile.PaneId);
        oldPlayer.Dispose();
        newTile.PlayerView = newPlayer;
        _playerByPane[newTile.PaneId] = newPlayer;
        return true;
    }

    private void WirePlayer(RoomTileViewModel tile, MpvPlayerView player)
    {
        player.SetMute(true);
        player.StateChanged += (_, _) =>
        {
            if (player.State == PlayerState.Playing)
            {
                _retryCounts.Remove(tile.PaneId);
                CancelRetryDelay(tile.PaneId);
                CancelOfflinePoll(tile.PaneId);
            }

            OnPlayerStateChanged(tile, player);
        };
        player.ReconnectRequested += (_, _) => ScheduleRetry(tile);
        player.SizeChanged += (_, _) => ScheduleQualityCheck(tile, player);
    }

    public async Task StartAsync(RoomTileViewModel tile, CancellationToken ct = default)
    {
        if (tile.IsPlaceholder)
        {
            return;
        }

        var player = EnsurePlayer(tile);
        player.RoomTitle = tile.DisplayTitle;

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_starts.Remove(tile.PaneId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _starts[tile.PaneId] = cts;
        }

        try
        {
            var room = await _services.Rooms.GetByIdAsync(tile.LiveRoomId, cts.Token);
            if (room is null)
            {
                player.ShowOffline("房间已被移除");
                return;
            }

            tile.Alias = room.Alias;
            tile.DisplayNumber = room.DisplayNumber;
            tile.Platform = room.Platform;
            tile.RoomId = room.RoomId;
            player.RoomTitle = tile.DisplayTitle;

            var provider = _services.Platforms.GetProvider(room.Platform);
            var cookie = await _services.Credentials.GetCookieAsync(room.Platform, cts.Token);

            // 20~30 路同时进入时，把真正的外部解析（B站 HTTP / yt-dlp 进程）限制为 4 路并发，
            // 避免 CLI 进程风暴；播放器起播本身不占这个信号量。
            RoomStatus status;
            // 这里不能用 ConfigureAwait(false)：后续 SetCompactMode/ShowFailed 会直接写
            // Avalonia 控件，必须回到调用方（UI）的 SynchronizationContext。
            await _parseGate.WaitAsync(cts.Token);
            try
            {
                status = await provider.GetRoomStatusAsync(room.RoomId, cookie, cts.Token);
            }
            finally
            {
                _parseGate.Release();
            }

            tile.Status = status;
            tile.StateText = status.IsLive ? "直播中" : "未开播";
            tile.DetailText = status.IsLive ? status.Title : "主播未开播，60 秒后重试";

            if (!status.IsLive || status.AvailableQualities.Count == 0)
            {
                player.ShowOffline("主播未开播");
                ScheduleOfflinePoll(tile);
                return;
            }

            _statuses[tile.PaneId] = status;
            _dropBaselines.Remove(tile.PaneId);
            var quality = ChooseQuality(status, player.Bounds.Height, ActiveRoomCount);
            if (quality is null)
            {
                player.ShowOffline("没有可用画质");
                return;
            }

            player.SetCompactMode(ShouldUseCompact(player.Bounds.Height, ActiveRoomCount));
            await player.PlayAsync(quality, cts.Token);
            // 起播时窗格可能还没完成布局（Bounds.Height=0），按 720P 起播；
            // 起播后拿到真实高度再纠正一次，单路/大窗才能升到原画。
            await ApplyQualityAsync(tile, player);
        }
        catch (OperationCanceledException)
        {
            // 切换方案/关闭窗格时的正常取消。
        }
        catch (Exception ex)
        {
            player.ShowFailed(ex.Message);
            tile.StateText = "播放失败";
            tile.DetailText = ex.Message;
            ScheduleRetry(tile);
        }
        finally
        {
            lock (_gate)
            {
                if (_starts.TryGetValue(tile.PaneId, out var current) && ReferenceEquals(current, cts))
                {
                    _starts.Remove(tile.PaneId);
                }
            }

            cts.Dispose();
        }
    }

    private int ActiveRoomCount => _statuses.Values.Count(status => status.IsLive);

    private static bool ShouldUseCompact(double height, int activeRoomCount)
        => QualityPolicy.ShouldUseCompact(height, activeRoomCount);

    private static StreamQuality? ChooseQuality(RoomStatus status, double height, int activeRoomCount)
        => QualityPolicy.Choose(status, height, activeRoomCount);

    private void ScheduleQualityCheck(RoomTileViewModel tile, MpvPlayerView player)
    {
        if (_disposed || player.State != PlayerState.Playing || !_statuses.ContainsKey(tile.PaneId))
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_qualityDebounces.Remove(tile.PaneId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cts = new CancellationTokenSource();
            _qualityDebounces[tile.PaneId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => _ = ApplyQualityAsync(tile, player));
            }
            catch (OperationCanceledException)
            {
                // 新一次 Bounds 变化覆盖了本次检查。
            }
            finally
            {
                lock (_gate)
                {
                    if (_qualityDebounces.TryGetValue(tile.PaneId, out var current) && ReferenceEquals(current, cts))
                    {
                        _qualityDebounces.Remove(tile.PaneId);
                    }
                }

                cts.Dispose();
            }
        });
    }

    private async Task ApplyQualityAsync(RoomTileViewModel tile, MpvPlayerView player)
    {
        if (_disposed || player.State != PlayerState.Playing ||
            !_statuses.TryGetValue(tile.PaneId, out var status) || !status.IsLive)
        {
            return;
        }

        var compact = ShouldUseCompact(player.Bounds.Height, ActiveRoomCount);
        player.SetCompactMode(compact);
        var target = ChooseQuality(status, player.Bounds.Height, ActiveRoomCount);
        if (target is null || player.CurrentQuality?.Id == target.Id)
        {
            return;
        }

        try
        {
            await player.SwitchQualityAsync(target);
        }
        catch (Exception)
        {
            // 切档失败保留当前流，下一轮巡检/尺寸变化会再试。
        }
    }

    private void ScheduleOfflinePoll(RoomTileViewModel tile, bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_offlinePolls.Remove(tile.PaneId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cts = new CancellationTokenSource();
            _offlinePolls[tile.PaneId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cts.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed && tile.PlayerView is not null &&
                        (force || tile.Status?.IsLive != true))
                    {
                        _ = StartAsync(tile);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 房间被移除或方案切换。
            }
            finally
            {
                lock (_gate)
                {
                    if (_offlinePolls.TryGetValue(tile.PaneId, out var current) && ReferenceEquals(current, cts))
                    {
                        _offlinePolls.Remove(tile.PaneId);
                    }
                }

                cts.Dispose();
            }
        });
    }

    private void InspectDrops()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (paneId, player) in _playerByPane.ToArray())
        {
            if (player.State != PlayerState.Playing || !_statuses.TryGetValue(paneId, out var status) || !status.IsLive)
            {
                _dropBaselines.Remove(paneId);
                continue;
            }

            var metrics = player.GetHealthMetrics();
            var drops = metrics.DecoderDroppedFrames + metrics.DroppedFrames;
            if (!_dropBaselines.TryGetValue(paneId, out var baseline))
            {
                _dropBaselines[paneId] = new DropBaseline(drops, now, 0);
                continue;
            }

            var elapsed = (now - baseline.SampledAt).TotalSeconds;
            if (elapsed <= 0)
            {
                continue;
            }

            var delta = Math.Max(0, drops - baseline.Drops);
            var fps = metrics.FramesPerSecond > 1 ? metrics.FramesPerSecond : 30;
            var dropRate = delta / (fps * elapsed);
            var badCycles = dropRate > 0.05 ? baseline.BadCycles + 1 : 0;

            if (badCycles >= 3)
            {
                _dropBaselines.Remove(paneId);
                _ = DowngradeAsync(player, status);
                continue;
            }

            _dropBaselines[paneId] = new DropBaseline(drops, now, badCycles);
        }
    }

    private static async Task DowngradeAsync(MpvPlayerView player, RoomStatus status)
    {
        var current = player.CurrentQuality;
        if (current is null)
        {
            return;
        }

        var lower = status.AvailableQualities
            .Where(quality => quality.Height < current.Height)
            .OrderByDescending(quality => quality.Height)
            .ThenByDescending(quality => quality.Fps)
            .FirstOrDefault();

        if (lower is not null)
        {
            try
            {
                await player.SwitchQualityAsync(lower);
                return;
            }
            catch (Exception)
            {
                // 降档失败则退到 15fps 紧凑模式。
            }
        }

        player.SetCompactMode(true);
    }

    public void Stop(string paneId)
    {
        lock (_gate)
        {
            if (_starts.Remove(paneId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (_offlinePolls.Remove(paneId, out var offline))
            {
                offline.Cancel();
                offline.Dispose();
            }

            if (_qualityDebounces.Remove(paneId, out var quality))
            {
                quality.Cancel();
                quality.Dispose();
            }

            if (_retryDelays.Remove(paneId, out var retry))
            {
                retry.Cancel();
                retry.Dispose();
            }

            _statuses.Remove(paneId);
            _dropBaselines.Remove(paneId);
            _retryCounts.Remove(paneId);
            _playerByPane.Remove(paneId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dropTimer.Stop();

        lock (_gate)
        {
            foreach (var cts in _starts.Values
                         .Concat(_offlinePolls.Values)
                         .Concat(_qualityDebounces.Values)
                         .Concat(_retryDelays.Values))
            {
                cts.Cancel();
                cts.Dispose();
            }

            _starts.Clear();
            _offlinePolls.Clear();
            _qualityDebounces.Clear();
            _retryDelays.Clear();
            _statuses.Clear();
            _dropBaselines.Clear();
            _retryCounts.Clear();
            _playerByPane.Clear();
        }

        _parseGate.Dispose();
    }

    private void ScheduleRetry(RoomTileViewModel tile)
    {
        if (_disposed)
        {
            return;
        }

        int attempt;
        CancellationTokenSource? cts = null;
        var exhausted = false;

        lock (_gate)
        {
            if (!_playerByPane.ContainsKey(tile.PaneId) || _retryDelays.ContainsKey(tile.PaneId))
            {
                return;
            }

            _retryCounts.TryGetValue(tile.PaneId, out var previous);
            attempt = previous + 1;
            if (attempt > ReconnectPolicy.MaxAttempts)
            {
                _retryCounts.Remove(tile.PaneId);
                exhausted = true;
            }
            else
            {
                _retryCounts[tile.PaneId] = attempt;
                cts = new CancellationTokenSource();
                _retryDelays[tile.PaneId] = cts;
            }
        }

        if (exhausted)
        {
            ScheduleFailureRecheck(tile);
            return;
        }

        _ = RetryAfterDelayAsync(tile, attempt, cts!);
    }

    private async Task RetryAfterDelayAsync(
        RoomTileViewModel tile,
        int attempt,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(ReconnectPolicy.DelayForAttempt(attempt), cts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (!_retryDelays.TryGetValue(tile.PaneId, out var current) ||
                        !ReferenceEquals(current, cts))
                    {
                        return;
                    }

                    _retryDelays.Remove(tile.PaneId);
                }

                if (!_disposed && _playerByPane.ContainsKey(tile.PaneId))
                {
                    _ = StartAsync(tile);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 窗格被移除、方案切换或已经恢复播放。
        }
        finally
        {
            lock (_gate)
            {
                if (_retryDelays.TryGetValue(tile.PaneId, out var current) &&
                    ReferenceEquals(current, cts))
                {
                    _retryDelays.Remove(tile.PaneId);
                }
            }

            cts.Dispose();
        }
    }

    private void ScheduleFailureRecheck(RoomTileViewModel tile)
    {
        if (_disposed || !_playerByPane.ContainsKey(tile.PaneId))
        {
            return;
        }

        tile.DetailText = "播放失败，60 秒后复检";
        ScheduleOfflinePoll(tile, force: true);
    }

    private void CancelRetryDelay(string paneId)
    {
        lock (_gate)
        {
            if (_retryDelays.Remove(paneId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private void CancelOfflinePoll(string paneId)
    {
        lock (_gate)
        {
            if (_offlinePolls.Remove(paneId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private static void OnPlayerStateChanged(RoomTileViewModel tile, MpvPlayerView player)
    {
        void Update()
        {
            tile.StateText = player.State switch
            {
                PlayerState.Loading => "连接中",
                PlayerState.Playing => "直播中",
                PlayerState.Offline => "已停止",
                PlayerState.Failed => "播放失败",
                _ => player.State.ToString()
            };
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.UIThread.Post(Update);
        }
    }
}
