using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台左侧「高能推荐」列表的一行：录制内偏移时间、弹幕速率/基线速率与触发原因。
/// 点击「跳转」由 <see cref="ClipStudioViewModel.JumpToHighlight"/> 把播放头与入出点跳到推荐区间；
/// V0.5-A 起卡片还承载该高能片段的语音转写状态、结果摘要与失败消息。
/// </summary>
public sealed partial class ClipHighlightItemViewModel : ObservableObject
{
    private const int SummaryMaxLength = 60;

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

    // ---- 语音识别（V0.5-A） ----

    /// <summary>本卡片是否正在转写（转写中按钮禁用并显示「转写中…」）。</summary>
    [ObservableProperty]
    private bool _isTranscribing;

    /// <summary>
    /// 卡片「🎙 转写」按钮是否可用：由 <see cref="ClipStudioViewModel"/> 按全局互斥状态统一刷新，
    /// 保证同一时刻只有一个转写任务时其它卡片按钮也禁用。
    /// </summary>
    [ObservableProperty]
    private bool _canTranscribe = true;

    /// <summary>本高能片段的转写结果；null 表示尚未转写或切换视频后已被清空。</summary>
    [ObservableProperty]
    private AsrTranscriptResult? _asrResult;

    /// <summary>转写失败时的中文消息；null 表示无错误。</summary>
    [ObservableProperty]
    private string? _asrError;

    public bool HasAsrResult => AsrResult is not null;

    public bool HasAsrError => !string.IsNullOrEmpty(AsrError);

    public string TranscribeButtonText => IsTranscribing ? "转写中…" : "转写";

    /// <summary>卡片摘要：完整文本 Trim 后截断到约 60 字符，空文本回退为占位提示。</summary>
    public string AsrSummary
    {
        get
        {
            var text = AsrResult?.FullText?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return "（未识别到文本）";
            }

            return text.Length <= SummaryMaxLength ? text : text[..SummaryMaxLength] + "…";
        }
    }

    /// <summary>摘要按钮的悬浮提示：展示完整识别文本（不截断）。</summary>
    public string AsrSummaryToolTip
    {
        get
        {
            var text = AsrResult?.FullText?.Trim();
            return string.IsNullOrEmpty(text) ? "（未识别到文本）" : text;
        }
    }

    partial void OnIsTranscribingChanged(bool value) => OnPropertyChanged(nameof(TranscribeButtonText));

    partial void OnAsrResultChanged(AsrTranscriptResult? value)
    {
        OnPropertyChanged(nameof(HasAsrResult));
        OnPropertyChanged(nameof(AsrSummary));
        OnPropertyChanged(nameof(AsrSummaryToolTip));
    }

    partial void OnAsrErrorChanged(string? value) => OnPropertyChanged(nameof(HasAsrError));
}
