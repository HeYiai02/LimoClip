using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiveMonitor.Desktop.Views;

public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow()
    {
        InitializeComponent();
    }

    public StartupErrorWindow(string title, string details) : this()
    {
        Title = title;
        ErrorText.Text = details;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(ErrorText.Text ?? string.Empty);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
