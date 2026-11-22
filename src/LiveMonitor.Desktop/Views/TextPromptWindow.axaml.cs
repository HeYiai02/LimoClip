using Avalonia.Controls;

namespace LiveMonitor.Desktop.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
    }

    public TextPromptWindow(string prompt, string defaultValue = "") : this()
    {
        PromptText.Text = prompt;
        ValueBox.Text = defaultValue;
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close(string.IsNullOrWhiteSpace(ValueBox.Text) ? null : ValueBox.Text.Trim());
}
