using Avalonia.Controls;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>
/// 焦点全屏渲染不变式的视图模型级回归测试：焦点进出必须同时改变
/// Dock/自由画布的可见性与「是否继续向后台窗格推弹幕」，防止再次出现
/// 「焦点浮层与后台工作区双重渲染导致弹幕掉帧」的回归。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class FocusRenderInvariantTests
{
    private const string TestClassName = nameof(FocusRenderInvariantTests);

    [Fact]
    public async Task Focus_enter_hides_workspace_and_stops_pane_danmaku_push()
    {
        LiveRoom first = null!;
        LiveRoom second = null!;
        await ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context =>
            {
                first = await ViewModelTestHarness.AddRoomAsync(context, 1, "主播一");
                second = await ViewModelTestHarness.AddRoomAsync(context, 2, "主播二");
            },
            async (_, viewModel) =>
            {
                await viewModel.AddRoomToWorkspaceAsync(first.Id, startPlayback: false);
                await viewModel.AddRoomToWorkspaceAsync(second.Id, startPlayback: false);

                var tiles = viewModel.WorkspaceTiles.Where(tile => tile.HasRoom).ToArray();
                Assert.Equal(2, tiles.Length);
                var focused = tiles[0];
                var focusedPlayer = new RecordingWorkspacePlayer();
                var otherPlayer = new RecordingWorkspacePlayer();
                focused.PlayerView = focusedPlayer;
                tiles[1].PlayerView = otherPlayer;

                Assert.True(viewModel.ShowDockWorkspace);
                Assert.True(viewModel.ShouldPushDanmakuToTiles);

                viewModel.ApplyFocusTileState(focused);
                try
                {
                    Assert.True(viewModel.IsFocused);
                    Assert.False(viewModel.ShowDockWorkspace);
                    Assert.False(viewModel.ShowFreeformCanvas);
                    Assert.False(viewModel.ShouldPushDanmakuToTiles);

                    viewModel.DispatchDanmaku(focused, [CreateMessage(focused)]);
                    Assert.Empty(focusedPlayer.Danmaku);
                    Assert.Empty(otherPlayer.Danmaku);
                }
                finally
                {
                    viewModel.ExitFocus();
                }

                Assert.False(viewModel.IsFocused);
                Assert.True(viewModel.ShowDockWorkspace);
                Assert.True(viewModel.ShouldPushDanmakuToTiles);

                viewModel.DispatchDanmaku(focused, [CreateMessage(focused)]);
                Assert.Single(focusedPlayer.Danmaku);
                Assert.Empty(otherPlayer.Danmaku);
            });
    }

    [Fact]
    public async Task Focus_enter_in_freeform_mode_hides_canvas_and_restores_on_exit()
    {
        LiveRoom room = null!;
        await ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context => room = await ViewModelTestHarness.AddRoomAsync(context, 1, "自由布局主播"),
            async (_, viewModel) =>
            {
                await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
                viewModel.ToggleFreeformMode();
                var tile = viewModel.WorkspaceTiles.First(tile => tile.HasRoom);

                Assert.True(viewModel.IsFreeformMode);
                Assert.True(viewModel.ShowFreeformCanvas);
                Assert.False(viewModel.ShowDockWorkspace);

                viewModel.ApplyFocusTileState(tile);
                try
                {
                    Assert.False(viewModel.ShowFreeformCanvas);
                    Assert.False(viewModel.ShowDockWorkspace);
                }
                finally
                {
                    viewModel.ExitFocus();
                }

                Assert.True(viewModel.IsFreeformMode);
                Assert.True(viewModel.ShowFreeformCanvas);
                Assert.False(viewModel.ShowDockWorkspace);
            });
    }

    [Fact]
    public async Task Focus_push_gate_only_applies_to_focused_pane()
    {
        LiveRoom room = null!;
        await ViewModelTestHarness.RunMainWindowAsync(
            TestClassName,
            async context => room = await ViewModelTestHarness.AddRoomAsync(context, 1, "单窗格主播"),
            async (_, viewModel) =>
            {
                await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
                var tile = viewModel.WorkspaceTiles.First(tile => tile.HasRoom);
                var player = new RecordingWorkspacePlayer();
                tile.PlayerView = player;

                viewModel.ApplyFocusTileState(tile);
                try
                {
                    viewModel.DispatchDanmaku(tile, [CreateMessage(tile)]);
                    Assert.Empty(player.Danmaku);
                }
                finally
                {
                    viewModel.ExitFocus();
                }

                viewModel.DispatchDanmaku(tile, [CreateMessage(tile)]);
                Assert.Single(player.Danmaku);
            });
    }

    private static DanmakuMessage CreateMessage(RoomTileViewModel tile) =>
        new(
            PlatformType.CustomStream,
            tile.RoomId,
            "uid",
            "观众",
            "测试弹幕",
            DateTimeOffset.UtcNow);

    /// <summary>只关心弹幕推送记录的假播放器，其余成员按契约返回空实现。</summary>
    private sealed class RecordingWorkspacePlayer : IWorkspacePlayer
    {
        private readonly Avalonia.Controls.Control _view = new ContentControl();

        public List<IReadOnlyList<DanmakuMessage>> Danmaku { get; } = [];

        public Avalonia.Controls.Control? View => _view;

        public string? RoomTitle { get; set; }

        public bool IsMuted { get; private set; }

        public int Volume { get; private set; }

        public bool DanmakuMasterEnabled { get; set; } = true;

        public bool DanmakuStickersEnabled { get; set; } = true;

        public bool DanmakuEmoteImagesEnabled { get; set; } = true;

        public PlayerState State { get; private set; } = PlayerState.Loading;

        public StreamQuality? CurrentQuality { get; private set; }

#pragma warning disable CS0067 // 假播放器不主动触发事件。
        public event EventHandler? StateChanged;
        public event EventHandler? ReconnectRequested;
        public event EventHandler? RefreshRequested;
#pragma warning restore CS0067

        public Task PlayAsync(StreamQuality quality, CancellationToken ct = default)
        {
            CurrentQuality = quality;
            State = PlayerState.Playing;
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
            return Task.CompletedTask;
        }

        public void SetMute(bool muted) => IsMuted = muted;

        public void SetVolume(int volume) => Volume = volume;

        public void SetCompactMode(bool enabled)
        {
        }

        public PlayerHealthMetrics GetHealthMetrics() => PlayerHealthMetrics.Empty;

        public void PushDanmaku(IReadOnlyList<DanmakuMessage> messages) => Danmaku.Add(messages);

        public void ClearDanmaku() => Danmaku.Clear();

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

        public bool TryAdoptSessionFrom(IWorkspacePlayer source, string? detail = null) => false;

        public void Dispose()
        {
        }
    }
}
