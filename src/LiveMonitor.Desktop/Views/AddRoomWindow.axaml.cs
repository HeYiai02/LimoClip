using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms.CustomStream;

namespace LiveMonitor.Desktop.Views;

public partial class AddRoomWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public AddRoomWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        CustomStreamCheck.IsCheckedChanged += (_, _) =>
        {
            CustomHeadersPanel.IsVisible = CustomStreamCheck.IsChecked == true;
        };
        UrlBox.TextChanged += OnUrlChanged;
    }

    private void OnUrlChanged(object? sender, TextChangedEventArgs e)
    {
        // 自动勾选：识别到 .m3u8/.flv/.ts/rtmp:// 等清流特征时默认按清流添加；
        // 无后缀 CDN Token 链接由用户手动勾选兜底。
        if (CustomStreamCheck.IsChecked != true && CustomStreamProvider.LooksLikeStreamUrl(UrlBox.Text))
        {
            CustomStreamCheck.IsChecked = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        ErrorText.Text = string.Empty;

        try
        {
            var group = GroupBox.Text?.Trim();
            var customStream = CustomStreamCheck.IsChecked == true;
            if (customStream)
            {
                await _viewModel.AddRoomAsync(
                    UrlBox.Text ?? string.Empty,
                    group,
                    customStream: true,
                    referer: RefererBox.Text,
                    userAgent: UserAgentBox.Text);
            }
            else
            {
                await _viewModel.AddRoomAsync(UrlBox.Text ?? string.Empty, group);
            }

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
