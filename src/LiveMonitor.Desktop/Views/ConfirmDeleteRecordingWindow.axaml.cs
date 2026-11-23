using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

/// <summary>
/// 删除历史录像前的确认窗口：告知会连同 .mkv 视频与 .jsonl 弹幕文件一起删除。
/// 返回 true 表示用户确认删除，false/null 表示取消。
/// </summary>
public partial class ConfirmDeleteRecordingWindow : Window
{
    public ConfirmDeleteRecordingWindow()
    {
        InitializeComponent();
    }

    public ConfirmDeleteRecordingWindow(RecordingHistoryItemViewModel item)
        : this()
    {
        DataContext = item;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Close(true);
}
