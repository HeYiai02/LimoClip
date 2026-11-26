using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>左侧主库的一行。只负责展示，不持有播放器。</summary>
public sealed class RoomListItemViewModel : ObservableObject
{
    private bool? _isLive;

    public RoomListItemViewModel(LiveRoom room)
    {
        Room = room;
    }

    public LiveRoom Room { get; }

    public Guid Id => Room.Id;

    public string DisplayTitle => $"#{Room.DisplayNumber} {Room.Alias}";

    /// <summary>平台徽章文字。</summary>
    public string PlatformBadge => PlatformBadgeFormat.Text(Room.Platform);

    public string PlatformBadgeBackground => PlatformBadgeFormat.Background(Room.Platform);

    public string PlatformBadgeForeground => PlatformBadgeFormat.Foreground(Room.Platform);

    /// <summary>null 表示尚未拿到状态，不应显示成在线。</summary>
    public bool? IsLive
    {
        get => _isLive;
        private set
        {
            if (SetProperty(ref _isLive, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusTip));
            }
        }
    }

    public string StatusBrush => _isLive switch
    {
        true => "#3DDC97",
        false => "#5B6472",
        null => "#3A414D"
    };

    public string StatusTip => _isLive switch
    {
        true => "直播中",
        false => "未开播",
        null => "在线状态未知（双击放入工作区后检测）"
    };

    public string Meta => string.IsNullOrWhiteSpace(Room.GroupTag)
        ? Room.RoomId
        : $"{Room.GroupTag} · {Room.RoomId}";

    public string GroupTag => Room.GroupTag ?? string.Empty;

    public string SearchText => $"{Room.DisplayNumber} {Room.Alias} {Room.RoomId} {Room.Platform} {Room.GroupTag}";

    public void SetLiveStatus(bool? isLive) => IsLive = isLive;
}
