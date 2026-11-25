using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.Player;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 一个 Dock 窗格的绑定数据。PlayerView 是 UI 控件，必须排除在 DockLayoutJson 之外，
/// 否则 System.Text.Json 会顺着控件对象图序列化到 Avalonia 内部，产生循环引用。
/// </summary>
public sealed class RoomTileViewModel : ObservableObject
{
    private string _stateText = "空窗格";
    private string _detailText = "从左侧双击直播间，或拖入此窗格";
    private bool _isDanmakuVisible;
    private IWorkspacePlayer? _playerView;

    public RoomTileViewModel()
    {
    }

    public RoomTileViewModel(string paneId, LiveRoom? room)
    {
        PaneId = paneId;
        if (room is null)
        {
            return;
        }

        LiveRoomId = room.Id;
        Alias = room.Alias;
        DisplayNumber = room.DisplayNumber;
        Platform = room.Platform;
        RoomId = room.RoomId;
        StateText = "等待起播";
        DetailText = "正在读取房间状态…";
    }

    public event EventHandler? FocusRequested;

    public event EventHandler? RemoveRequested;

    public event EventHandler<RoomTileViewModel>? SwapRequested;

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public void RequestRemove() => RemoveRequested?.Invoke(this, EventArgs.Empty);

    public void RequestSwap(RoomTileViewModel target) => SwapRequested?.Invoke(this, target);

    /// <summary>对应 DockLayoutJson 中的窗格标识，例如 pane-1。</summary>
    /// <summary>把主库房间绑定到当前空窗格，并刷新所有派生展示属性。</summary>
    public void Assign(LiveRoom room)
    {
        LiveRoomId = room.Id;
        Alias = room.Alias;
        DisplayNumber = room.DisplayNumber;
        Platform = room.Platform;
        RoomId = room.RoomId;
        StateText = "等待起播";
        DetailText = "正在读取房间状态…";
        OnPropertyChanged(nameof(LiveRoomId));
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(DisplayNumber));
        OnPropertyChanged(nameof(Platform));
        OnPropertyChanged(nameof(RoomId));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(HasRoom));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(PlatformBadge));
    }

    /// <summary>只刷新主库元数据，不重置播放状态。</summary>
    public void UpdateMetadata(LiveRoom room)
    {
        if (LiveRoomId != room.Id)
        {
            return;
        }

        Alias = room.Alias;
        DisplayNumber = room.DisplayNumber;
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(DisplayNumber));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    /// <summary>方案切换时把窗格恢复为空窗格，不销毁 PlayerView（由调用方决定）。</summary>
    public void ClearRoom()
    {
        LiveRoomId = Guid.Empty;
        Alias = string.Empty;
        DisplayNumber = 0;
        Platform = default;
        RoomId = string.Empty;
        Status = null;
        StateText = string.Empty;
        DetailText = string.Empty;
        OnPropertyChanged(nameof(LiveRoomId));
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(DisplayNumber));
        OnPropertyChanged(nameof(Platform));
        OnPropertyChanged(nameof(RoomId));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(HasRoom));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(PlatformBadge));
    }

    public string PaneId { get; set; } = string.Empty;

    public Guid LiveRoomId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public int DisplayNumber { get; set; }

    public PlatformType Platform { get; set; }

    public string RoomId { get; set; } = string.Empty;

    public string StateText
    {
        get => _stateText;
        set => SetProperty(ref _stateText, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    /// <summary>弹幕渲染开关；V0.1 只持久化与显示占位，V0.2 接实时弹幕。</summary>
    public bool IsDanmakuVisible
    {
        get => _isDanmakuVisible;
        set => SetProperty(ref _isDanmakuVisible, value);
    }

    /// <summary>播放器句柄。Dock 序列化必须忽略；方案切换时由 WorkspacePlayerManager 重新挂载。</summary>
    [JsonIgnore]
    public IWorkspacePlayer? PlayerView
    {
        get => _playerView;
        set
        {
            if (SetProperty(ref _playerView, value))
            {
                // 播放器底部控制条绑定本窗格的弹幕开关；PlayerView 不参与 Dock 序列化。
                if (value?.View is { } view)
                {
                    view.DataContext = this;
                }

                OnPropertyChanged(nameof(HasPlayer));
            }
        }
    }

    [JsonIgnore]
    public RoomStatus? Status { get; set; }

    [JsonIgnore]
    public bool IsPlaceholder => LiveRoomId == Guid.Empty;

    [JsonIgnore]
    public bool HasRoom => LiveRoomId != Guid.Empty;

    [JsonIgnore]
    public bool HasPlayer => _playerView is not null;

    [JsonIgnore]
    public string DisplayTitle => DisplayNumber > 0 ? $"#{DisplayNumber} {Alias}" : Alias;

    [JsonIgnore]
    public string PlatformBadge => Platform switch
    {
        PlatformType.Bilibili => "B站",
        PlatformType.Twitch => "Twitch",
        PlatformType.Douyin => "抖音",
        PlatformType.Douyu => "斗鱼",
        PlatformType.Huya => "虎牙",
        PlatformType.YouTube => "YouTube",
        PlatformType.CustomStream => "清流",
        _ => "—"
    };
}
