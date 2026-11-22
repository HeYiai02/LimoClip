using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveMonitor.Desktop.ViewModels;

namespace LiveMonitor.Desktop.Views;

public partial class RoomTileView : Avalonia.Controls.UserControl
{
    private DateTime _lastClickAt;
    private RoomTileViewModel? _dragSource;
    private Avalonia.Point _dragStart;

    public RoomTileView()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source is Border { Name: "DragHandle" } ||
             source.FindAncestorOfType<Border>()?.Name == "DragHandle"))
        {
            return;
        }

        if (DataContext is not RoomTileViewModel tile || tile.IsPlaceholder)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastClickAt).TotalMilliseconds <= 500)
        {
            _lastClickAt = default;
            tile.RequestFocus();
            e.Handled = true;
            return;
        }

        _lastClickAt = now;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RoomTileViewModel tile)
        {
            tile.RequestFocus();
            e.Handled = true;
        }
    }

    private void OnFocusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RoomTileViewModel tile)
        {
            tile.RequestFocus();
            e.Handled = true;
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RoomTileViewModel tile)
        {
            tile.RequestRemove();
            e.Handled = true;
        }
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RoomTileViewModel tile || tile.IsPlaceholder ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragSource = tile;
        _dragStart = e.GetPosition(this);
        if (sender is IInputElement handle)
        {
            e.Pointer.Capture(handle);
        }

        e.Handled = true;
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);

        var source = _dragSource;
        _dragSource = null;
        if (source is null || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var hit = top.InputHitTest(e.GetPosition(top));
        var targetView = FindTile(hit as Visual);
        if (targetView?.DataContext is RoomTileViewModel target &&
            !ReferenceEquals(source, target) && target.HasRoom)
        {
            source.RequestSwap(target);
        }

        e.Handled = true;
    }

    private static RoomTileView? FindTile(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is RoomTileView tile)
            {
                return tile;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }
}
