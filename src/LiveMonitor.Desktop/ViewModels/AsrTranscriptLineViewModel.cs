using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台右侧「语音识别」面板中的一行：相对转写片段起点的时间码 + 单句文本。
/// 点击整行由 View 调 <see cref="ClipAsrViewModel.SeekToLine"/> 跳转预览位置，
/// 因此本类只承载只读展示数据，不引用 Avalonia 控件。
/// </summary>
public sealed class AsrTranscriptLineViewModel
{
    public AsrTranscriptLineViewModel(AsrTranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        RelativeStart = segment.RelativeStart < TimeSpan.Zero ? TimeSpan.Zero : segment.RelativeStart;
        Text = segment.Text ?? string.Empty;
    }

    /// <summary>相对本次转写片段起点的偏移；绝对时间 = 结果 ClipStart + 本值。</summary>
    public TimeSpan RelativeStart { get; }

    /// <summary>秒级时间码（mm:ss 或 h:mm:ss），与导出任务里的时间显示保持一致。</summary>
    public string TimeText => ClipExportTaskViewModel.FormatTime(RelativeStart);

    /// <summary>该句识别文本。</summary>
    public string Text { get; }
}
