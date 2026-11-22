using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

public partial class EditRoomWindow : Window
{
    public EditRoomWindow()
    {
        InitializeComponent();
    }

    public EditRoomWindow(RoomListItemViewModel item) : this()
    {
        AliasBox.Text = item.Room.Alias;
        GroupBox.Text = item.Room.GroupTag ?? string.Empty;
        NumberBox.Text = item.Room.DisplayNumber.ToString();
    }

    public string AliasText => (AliasBox.Text ?? string.Empty).Trim();

    public string? GroupText => string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim();

    public int DisplayNumber => int.TryParse(NumberBox.Text, out var value) ? value : 0;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AliasText))
        {
            ErrorText.Text = "显示名称不能为空。";
            return;
        }

        if (DisplayNumber <= 0)
        {
            ErrorText.Text = "序号必须是大于 0 的整数。";
            return;
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
