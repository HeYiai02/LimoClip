using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Platforms.CustomStream;

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

        IsCustomStream = item.Room.Platform == PlatformType.CustomStream;
        if (IsCustomStream)
        {
            CustomStreamPanel.IsVisible = true;
            StreamUrlBox.Text = item.Room.RawUrl;
            var headers = CustomStreamProvider.ParseHeadersJson(item.Room.CustomHeadersJson);
            headers.TryGetValue("Referer", out var referer);
            headers.TryGetValue("User-Agent", out var userAgent);
            RefererBox.Text = referer ?? string.Empty;
            UserAgentBox.Text = userAgent ?? string.Empty;
        }
    }

    public bool IsCustomStream { get; }

    public string AliasText => (AliasBox.Text ?? string.Empty).Trim();

    public string? GroupText => string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim();

    public int DisplayNumber => int.TryParse(NumberBox.Text, out var value) ? value : 0;

    /// <summary>清流可编辑的拉流地址（非清流返回 null）。</summary>
    public string? RawUrl => IsCustomStream ? (StreamUrlBox.Text ?? string.Empty).Trim() : null;

    public string? RefererText => IsCustomStream ? RefererBox.Text : null;

    public string? UserAgentText => IsCustomStream ? UserAgentBox.Text : null;

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

        if (IsCustomStream && !CustomStreamProvider.IsSupportedDirectUrl(RawUrl))
        {
            ErrorText.Text = "清流地址必须是 http(s) / rtmp(s) / rtsp(s) / srt 或 mms 绝对地址。";
            return;
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
