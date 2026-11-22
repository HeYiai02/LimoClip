using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiveMonitor.Desktop.Views;

public partial class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow()
    {
        InitializeComponent();
    }

    public ConfirmDeleteWindow(string roomName) : this()
    {
        MessageText.Text = $"确定要从主库删除「{roomName}」吗？";
    }

    private void OnDelete(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
