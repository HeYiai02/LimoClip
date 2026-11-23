using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台左侧「高能推荐」列表的一行：录制内偏移时间、弹幕速率/基线速率与触发原因。
/// 点击后由 <see cref="ClipStudioViewModel.JumpToHighlight"/> 把播放头与入出点跳到推荐区间。
/// </summary>
public sealed partial class ClipHighlightItemViewModel : ObservableObject
{
    public ClipHighlightItemViewModel(HighlightEvent highlightEvent, TimeSpan position)
    {
        HighlightEvent = highlightEvent ?? throw new ArgumentNullException(nameof(highlightEvent));
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    public HighlightEvent HighlightEvent { get; }

    /// <summary>相对录像起点的偏移（已按会话起始时间回算并夹到 [0, duration]）。</summary>
    public TimeSpan Position { get; }

    public string TimeText => ClipExportTaskViewModel.FormatTime(Position);

    public string RateText => $"{HighlightEvent.CurrentRatePerSec:0.0}/s";

    public string BaselineText => $"基线 {HighlightEvent.BaselineRatePerSec:0.0}/s";

    public string Reason => HighlightEvent.TriggerReason;

    public string ToolTipText =>
        $"{TimeText} · {RateText}（{BaselineText}）{Environment.NewLine}{Reason}";

    [ObservableProperty]
    private bool _isSelected;
}
