using Avalonia.Controls;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// WorkspacePlayerManager 的接缝测试：通过 internal 工厂注入假播放器，验证槽位句柄的
/// 复用/迁移/释放与 Dispose 语义，不启动 mpv、不访问网络。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class WorkspacePlayerManagerTests
{
    private const string TestClassName = nameof(WorkspacePlayerManagerTests);

    [Fact]
    public async Task EnsurePlayer_SamePane_ReusesSameHandle()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            using var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var tile = CreateTile("pane-1");

            var first = manager.EnsurePlayer(tile);
            var second = manager.EnsurePlayer(tile);

            Assert.Same(first, second);
            Assert.Same(first, tile.PlayerView);
            Assert.Equal(1, factory.CreatedCount);
            Assert.True(first.IsMuted, "WirePlayer 应默认静音窗格播放器");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EnsurePlayer_DifferentPanes_CreateIndependentHandles()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            using var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var first = CreateTile("pane-1");
            var second = CreateTile("pane-2");

            var firstPlayer = manager.EnsurePlayer(first);
            var secondPlayer = manager.EnsurePlayer(second);

            Assert.NotSame(firstPlayer, secondPlayer);
            Assert.Same(firstPlayer, first.PlayerView);
            Assert.Same(secondPlayer, second.PlayerView);
            Assert.Equal(2, factory.CreatedCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EnsurePlayer_ExistingTilePlayer_IsAdoptedWithoutCreatingNew()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var tile = CreateTile("pane-1");
            var existing = new FakeWorkspacePlayer();
            tile.PlayerView = existing;

            var player = manager.EnsurePlayer(tile);

            Assert.Same(existing, player);
            Assert.Equal(0, factory.CreatedCount);
            // 已存在的句柄同样纳入跟踪：管理器 Dispose 时兜底释放。
            manager.Dispose();
            Assert.Equal(1, existing.DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TransferPlayer_MovesSessionToNewHandleAndDisposesOldOne()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var oldTile = CreateTile("pane-1");
            var newTile = CreateTile("pane-2");
            oldTile.DetailText = "旧窗格详情";
            var oldPlayer = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(oldTile));
            var session = new object();
            oldPlayer.SetSession(session);

            var transferred = manager.TransferPlayer(oldTile, newTile);

            Assert.True(transferred);
            var newPlayer = Assert.IsType<FakeWorkspacePlayer>(newTile.PlayerView);
            Assert.NotSame(oldPlayer, newPlayer);
            Assert.Same(session, newPlayer.Session);
            Assert.Null(oldPlayer.Session);
            Assert.Equal(1, oldPlayer.DisposeCount);
            Assert.Null(oldTile.PlayerView);
            Assert.Equal(2, factory.CreatedCount);
            Assert.Equal(oldTile.DetailText, newPlayer.LastAdoptDetail);

            manager.Dispose();
            Assert.Equal(1, newPlayer.DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TransferPlayer_WithoutSourcePlayer_ReturnsFalse()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            using var manager = new WorkspacePlayerManager(context.Services, factory.Create);

            var transferred = manager.TransferPlayer(CreateTile("pane-1"), CreateTile("pane-2"));

            Assert.False(transferred);
            Assert.Equal(0, factory.CreatedCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TransferPlayer_WithoutSession_DiscardsNewHandleAndKeepsOldOne()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var oldTile = CreateTile("pane-1");
            var newTile = CreateTile("pane-2");
            var oldPlayer = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(oldTile));

            var transferred = manager.TransferPlayer(oldTile, newTile);

            Assert.False(transferred);
            Assert.Same(oldPlayer, oldTile.PlayerView);
            Assert.Equal(0, oldPlayer.DisposeCount);
            Assert.Null(newTile.PlayerView);
            // 迁移失败时新建的句柄必须立即释放，不能泄漏。
            Assert.Equal(2, factory.CreatedCount);
            Assert.Equal(1, factory.Created[1].DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RemovePlayer_DisposesHandleAndClearsSlot_AndIsIdempotent()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var tile = CreateTile("pane-1");
            var player = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(tile));

            manager.RemovePlayer(tile);

            Assert.Null(tile.PlayerView);
            Assert.Equal(1, player.DisposeCount);

            // 重复移除不应再次释放已释放的句柄。
            manager.RemovePlayer(tile);
            Assert.Equal(1, player.DisposeCount);

            // 移除后再起播会创建新句柄，而不是复活旧句柄。
            var replacement = manager.EnsurePlayer(tile);
            Assert.NotSame(player, replacement);
            Assert.Equal(2, factory.CreatedCount);

            manager.Dispose();
            Assert.Equal(1, player.DisposeCount);
            Assert.Equal(1, factory.Created[1].DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Dispose_ReleasesAllTrackedHandles_AndIsIdempotent()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var first = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(CreateTile("pane-1")));
            var second = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(CreateTile("pane-2")));

            manager.Dispose();

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);

            manager.Dispose();

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Disposed_OperationsThrowObjectDisposedException()
    {
        await ViewModelTestHarness.RunServicesAsync(TestClassName, context =>
        {
            var factory = new FakePlayerFactory();
            var manager = new WorkspacePlayerManager(context.Services, factory.Create);
            var tile = CreateTile("pane-1");
            var player = Assert.IsType<FakeWorkspacePlayer>(manager.EnsurePlayer(tile));
            manager.Dispose();

            Assert.Throws<ObjectDisposedException>(() => manager.EnsurePlayer(CreateTile("pane-2")));
            Assert.Throws<ObjectDisposedException>(() => manager.TransferPlayer(CreateTile("pane-3"), CreateTile("pane-4")));
            Assert.Throws<ObjectDisposedException>(() => manager.RemovePlayer(tile));

            Assert.Equal(1, factory.CreatedCount);
            Assert.Equal(1, player.DisposeCount);
            return Task.CompletedTask;
        });
    }

    private static RoomTileViewModel CreateTile(string paneId) => new(paneId, null);

    private sealed class FakePlayerFactory
    {
        private readonly List<FakeWorkspacePlayer> _created = [];

        public IReadOnlyList<FakeWorkspacePlayer> Created => _created;

        public int CreatedCount => _created.Count;

        public IWorkspacePlayer Create()
        {
            var player = new FakeWorkspacePlayer();
            _created.Add(player);
            return player;
        }
    }

    /// <summary>假播放器：View 返回普通控件（不启动 mpv），用内存对象模拟可迁移会话。</summary>
    private sealed class FakeWorkspacePlayer : IWorkspacePlayer
    {
        private readonly Avalonia.Controls.ContentControl _view = new();
        private int _disposeCount;
        private int _setMuteCount;

        public object? Session { get; private set; }

        public string? LastAdoptDetail { get; private set; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int SetMuteCount => Volatile.Read(ref _setMuteCount);

        public Avalonia.Controls.Control? View => _view;

        public string? RoomTitle { get; set; }

        public bool IsMuted { get; private set; }

        public int Volume { get; private set; }

        public bool DanmakuMasterEnabled { get; set; } = true;

        public bool DanmakuStickersEnabled { get; set; } = true;

        public bool DanmakuEmoteImagesEnabled { get; set; } = true;

        public PlayerState State { get; set; } = PlayerState.Loading;

        public StreamQuality? CurrentQuality { get; set; }

#pragma warning disable CS0067 // 假播放器不主动触发事件；管理器订阅行为由 WirePlayer 覆盖。
        public event EventHandler? StateChanged;
        public event EventHandler? ReconnectRequested;
        public event EventHandler? RefreshRequested;
#pragma warning restore CS0067

        public void SetSession(object? session) => Session = session;

        public Task PlayAsync(StreamQuality quality, CancellationToken ct = default)
        {
            CurrentQuality = quality;
            State = PlayerState.Playing;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SwitchQualityAsync(StreamQuality quality, CancellationToken ct = default)
        {
            CurrentQuality = quality;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            State = PlayerState.Offline;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void SetMute(bool muted)
        {
            IsMuted = muted;
            Interlocked.Increment(ref _setMuteCount);
        }

        public void SetVolume(int volume) => Volume = volume;

        public void SetCompactMode(bool enabled)
        {
        }

        public PlayerHealthMetrics GetHealthMetrics() => PlayerHealthMetrics.Empty;

        public void PushDanmaku(IReadOnlyList<DanmakuMessage> messages)
        {
        }

        public void ClearDanmaku()
        {
        }

        public void SetChromeVisible(bool visible)
        {
        }

        public void SetDanmakuEnabled(bool enabled)
        {
        }

        public void ShowLoading(string? detail = null)
        {
        }

        public void ShowOffline(string? detail = null)
        {
        }

        public void ShowFailed(string? detail = null)
        {
        }

        public bool TryAdoptSessionFrom(IWorkspacePlayer source, string? detail = null)
        {
            // 模拟 MpvPlayerView 的实现内类型收窄：来源不兼容/无会话时失败且双方状态不变。
            if (source is not FakeWorkspacePlayer oldPlayer || oldPlayer.Session is not { } session)
            {
                return false;
            }

            oldPlayer.Session = null;
            Session = session;
            LastAdoptDetail = detail;
            return true;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
