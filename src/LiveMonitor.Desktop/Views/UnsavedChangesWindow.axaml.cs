using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(ProfileSwitchChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(ProfileSwitchChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(ProfileSwitchChoice.Cancel);
}
