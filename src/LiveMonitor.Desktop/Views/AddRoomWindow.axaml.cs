using Avalonia.Controls;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

public partial class AddRoomWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public AddRoomWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private async void OnAdd(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        ErrorText.Text = string.Empty;

        try
        {
            var group = GroupBox.Text?.Trim();
            await _viewModel.AddRoomAsync(UrlBox.Text ?? string.Empty, group);
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }
}
