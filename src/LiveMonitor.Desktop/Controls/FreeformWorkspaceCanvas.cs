using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using LiveMonitor.Desktop.Views;
using ContextMenu = Avalonia.Controls.ContextMenu;
using Cursor = Avalonia.Input.Cursor;
using DragEventArgs = Avalonia.Input.DragEventArgs;
using DragDropEffects = Avalonia.Input.DragDropEffects;
using MenuItem = Avalonia.Controls.MenuItem;
using Panel = Avalonia.Controls.Panel;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 自由画布：所有直播窗格都是同一个画布的子项，按归一化矩形定位，用 ZIndex 表达叠放。
/// 取代 Dock.Avalonia 的二叉树平铺（Dock 无法表达画中画重叠）。
/// </summary>
public sealed class FreeformWorkspaceCanvas : Panel
{
    /// <summary>房间拖拽负载格式：应用内字符串格式，拖放源与目标共用同一实例。</summary>
    internal static readonly DataFormat<string> RoomIdFormat =
        DataFormat.CreateStringApplicationFormat("limoclip.room-id");
    private const double PinnedZBase = 1000;
    private const int NormalZMax = 999;
    private const int PinnedZMax = 1999;
    private const int DraggingZIndex = 5000;

    private readonly Dictionary<string, FreeformTilePlacement> _placements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentControl> _childByPaneId = new(StringComparer.Ordinal);
    private readonly SnapGuidesOverlay _overlay = new();

    /// <summary>窗格内容模板：与 Dock 一致，用 RoomTileView 显示标题/播放器/控制条。</summary>
    private static readonly IDataTemplate TileTemplate = new FuncDataTemplate<RoomTileViewModel>(
        (tile, _) => tile is null ? null : new RoomTileView { DataContext = tile });

    private string? _pendingPaneId;
    private DragKind _pendingKind;
    private ResizeEdge _pendingEdge;
    private string? _activePaneId;
    private FreeformTilePlacement? _activePlacement;
    private NormalizedRect _originRect;
    private Point _pressPoint;
    private DragKind _dragKind;
    private ResizeEdge _resizeEdge;
    private bool _isFirstArrange = true;
    private bool _hasMoved;
    private IPointer? _capturedPointer;

    public FreeformWorkspaceCanvas()
    {
        ClipToBounds = true;
        _overlay.ZIndex = 9999;
        Children.Add(_overlay);

        // 用 Tunnel + handledEventsToo 接管指针：窗格内部控件（RoomTileView 的点击/双击处理）会
        // 把事件标记为已处理，普通冒泡阶段收不到，拖动就会完全无反应。
        AddHandler(PointerPressedEvent, OnCanvasPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnCanvasPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnCanvasPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnCanvasCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 接收从左侧直播间列表拖过来的房间。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDropDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDropDragLeave);
        AddHandler(DragDrop.DropEvent, OnDropDrop);
    }

    /// <summary>房间被从左侧列表拖放到画布时触发，Rect 为建议的落位（归一化）。</summary>
    public event EventHandler<RoomDropEventArgs>? RoomDropped;

    /// <summary>拖动/缩放/层级调整后触发，供 ViewModel 标脏与保存。</summary>
    public event EventHandler? LayoutChanged;

    public IReadOnlyList<FreeformTilePlacement> PlacementsSnapshot =>
        _placements.Values.Select(placement => placement.Clone()).ToList();

    /// <summary>直接指定某个窗格的归一化位置（拖放入口、外部预设用）。</summary>
    public void SetPlacement(string paneId, NormalizedRect rect)
    {
        if (!_placements.TryGetValue(paneId, out var placement))
        {
            return;
        }

        placement.SetRect(rect.ClampToCanvas());
        ApplyZIndexes();
        InvalidateArrange();
    }

    private enum DragKind
    {
        None,
        Move,
        Resize
    }

    /// <summary>按窗格列表增量同步子项：复用已有窗格、删除多余、缺失的创建并落到默认位置。</summary>
    public void SetTiles(IEnumerable<RoomTileViewModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var incoming = tiles.ToList();
        var incomingIds = new HashSet<string>(incoming.Select(tile => tile.PaneId), StringComparer.Ordinal);

        foreach (var stale in _childByPaneId.Keys.Where(paneId => !incomingIds.Contains(paneId)).ToList())
        {
            var child = _childByPaneId[stale];
            Children.Remove(child);
            _childByPaneId.Remove(stale);
            _placements.Remove(stale);
        }

        var ordered = incoming.OrderBy(tile => tile.PaneId, StringComparer.Ordinal).ToList();
        var count = Math.Max(1, ordered.Count);
        var autoRects = FreeformLayoutPresets.AutoFit(count, FreeformInteractionLogic.AspectOf(Bounds.Width, Bounds.Height))
            .Select(placement => placement.ToRect())
            .ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var tile = ordered[index];
            if (_childByPaneId.TryGetValue(tile.PaneId, out var existing))
            {
                existing.Content = tile;
                if (_placements.TryGetValue(tile.PaneId, out var existingPlacement))
                {
                    existing.ZIndex = existingPlacement.EffectiveZIndex;
                }

                continue;
            }

            var child = new ContentControl
            {
                Content = tile,
                ContentTemplate = TileTemplate,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            Children.Add(child);
            _childByPaneId[tile.PaneId] = child;

            if (!_placements.TryGetValue(tile.PaneId, out var placement))
            {
                var rect = index < autoRects.Count
                    ? autoRects[index]
                    : FreeformLayoutPresets.DefaultLanding(index, tile.PaneId).ToRect();
                // 新窗格必须拿到唯一的普通层级：否则与背景大窗同层时，
                // 视觉上小窗在上（后加入），命中判定却可能反向选中大窗。
                placement = FreeformTilePlacement.Create(
                    tile.PaneId,
                    rect,
                    zIndex: FreeformPaneHitTest.NextNormalZIndex(_placements.Values));
                _placements[tile.PaneId] = placement;
            }

            child.ZIndex = placement.EffectiveZIndex;
        }

        _overlay.ZIndex = 9999;
        // 历史布局文档可能留下重复层级：统一压成唯一序列，保证渲染顺序与命中顺序一致。
        NormalizeZIndexes();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>应用归一化布局文档；缺失窗格按默认落点兜底，多余放置项忽略。</summary>
    public void ApplyDocument(FreeformLayoutDocument? document)
    {
        if (document is null)
        {
            return;
        }

        var index = 0;
        foreach (var paneId in _childByPaneId.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList())
        {
            var placement = document.Tiles.FirstOrDefault(item => string.Equals(item.PaneId, paneId, StringComparison.Ordinal));
            var rect = placement?.ToRect() ?? FreeformLayoutPresets.DefaultLanding(index, paneId).ToRect();
            // 文档里缺失的窗格（例如刚开始录制后拖入的新窗格）不能用默认 ZIndex=1 兜底，
            // 否则会与铺满画布的背景大窗同层，命中时被大窗抢走。
            var target = placement ?? FreeformTilePlacement.Create(
                paneId,
                rect,
                zIndex: FreeformPaneHitTest.NextNormalZIndex(_placements.Values));
            target.PaneId = paneId;
            target.SetRect(rect);
            _placements[paneId] = target;
            index++;
        }

        // 旧文档中可能出现重复层级：归一化后再同步到子项 ZIndex。
        NormalizeZIndexes();
        _isFirstArrange = false;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>导出当前布局文档。</summary>
    public FreeformLayoutDocument? CaptureDocument()
    {
        if (_placements.Count == 0)
        {
            return null;
        }

        var placements = _placements.Values
            .OrderBy(placement => placement.PaneId, StringComparer.Ordinal)
            .Select(placement => placement.Clone())
            .ToList();
        return FreeformLayoutDocument.FromPlacements(placements);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in _childByPaneId.Values)
        {
            child.Measure(availableSize);
        }

        _overlay.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width <= 0 || finalSize.Height <= 0)
        {
            return finalSize;
        }

        if (_isFirstArrange && _placements.Count > 0 && _childByPaneId.Count > 0)
        {
            var autoRects = FreeformLayoutPresets.AutoFit(_childByPaneId.Count, FreeformInteractionLogic.AspectOf(finalSize.Width, finalSize.Height))
                .Select(item => item.ToRect())
                .ToList();
            var index = 0;
            foreach (var paneId in _childByPaneId.Keys.OrderBy(id => id, StringComparer.Ordinal))
            {
                var placement = _placements[paneId];
                if (index < autoRects.Count)
                {
                    placement.SetRect(autoRects[index]);
                }

                index++;
            }
        }

        _isFirstArrange = false;

        foreach (var (paneId, child) in _childByPaneId)
        {
            if (!_placements.TryGetValue(paneId, out var placement))
            {
                continue;
            }

            var (x, y, width, height) = placement.ToRect().ToPixels(finalSize.Width, finalSize.Height);
            // 留 1px 视觉缝，避免相邻窗格同底色时糊在一起（Panel.Render 在 Avalonia 11 是 sealed，不能自绘边框）。
            child.Arrange(new Rect(x + 1, y + 1, Math.Max(0, width - 2), Math.Max(0, height - 2)));
        }

        _overlay.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (IsInteractiveChildSource(e.Source))
        {
            return;
        }

        var position = e.GetPosition(this);
        var paneId = FindTopmostPaneAt(position);
        if (paneId is null || !_placements.TryGetValue(paneId, out var placement))
        {
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            ShowTileMenu(paneId, position);
            e.Handled = true;
            return;
        }

        BringToFront(paneId);

        if (placement.IsLocked)
        {
            return;
        }

        // 延迟到指针真正移动超过阈值后才开始拖动：这样单击/双击仍然交给 RoomTileView 处理，
        // 不会因为抢占指针而丢掉点击语义。
        _pressPoint = position;
        _pendingPaneId = paneId;
        _pendingEdge = HitTestResizeEdge(placement, position);
        _pendingKind = _pendingEdge == ResizeEdge.None ? DragKind.Move : DragKind.Resize;
        _originRect = placement.ToRect();
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(this);

        if (_pendingPaneId is not null)
        {
            var dx = position.X - _pressPoint.X;
            var dy = position.Y - _pressPoint.Y;
            if (!_hasMoved && !FreeformInteractionLogic.ExceedsDragThreshold(dx, dy))
            {
                return;
            }

            if (!_hasMoved)
            {
                BeginDrag(e.Pointer);
            }
        }

        if (_activePlacement is null || _activePaneId is null || _dragKind == DragKind.None)
        {
            UpdateCursor(position);
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var deltaX = (position.X - _pressPoint.X) / width;
        var deltaY = (position.Y - _pressPoint.Y) / height;
        var bypassSnap = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var lockAspect = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var others = _placements
            .Where(pair => !string.Equals(pair.Key, _activePaneId, StringComparison.Ordinal))
            .Select(pair => pair.Value.ToRect())
            .ToList();

        SnapResult result;
        if (_dragKind == DragKind.Move)
        {
            var raw = _originRect.WithPosition(_originRect.Left + deltaX, _originRect.Top + deltaY);
            result = FreeformSnapEngine.SnapMove(raw, others, width, height, bypassSnap: bypassSnap);
        }
        else
        {
            result = FreeformSnapEngine.SnapResize(
                _originRect,
                _resizeEdge,
                deltaX,
                deltaY,
                others,
                width,
                height,
                bypassSnap: bypassSnap,
                lockAspect16By9: lockAspect);
        }

        _activePlacement.SetRect(result.Rect);
        _overlay.SetGuides(result.Guides);
        _overlay.SetActiveRect(result.Rect);
        _overlay.SetHud(
            FreeformInteractionLogic.BuildDragHud(result.Rect, width, height, result.Guides.Count > 0),
            new Point(position.X + 16, position.Y + 20));
        Cursor = new Cursor(FreeformInteractionLogic.MapResizeCursor(_resizeEdge, _dragKind == DragKind.Move));
        InvalidateArrange();
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragKind != DragKind.None)
        {
            EndDrag(e.Pointer);
            e.Handled = true;
            return;
        }

        ClearPending();
    }

    private void OnCanvasCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(null);

    /// <summary>当前是否正在拖动或缩放窗格（供 Esc 取消使用）。</summary>
    public bool IsInteracting => _dragKind != DragKind.None;

    /// <summary>取消当前拖动/缩放，恢复到按下前的几何位置。</summary>
    public void CancelInteraction()
    {
        if (_dragKind == DragKind.None)
        {
            return;
        }

        _activePlacement?.SetRect(_originRect);
        _hasMoved = false;
        EndDrag(_capturedPointer);
        InvalidateArrange();
    }

    private void BeginDrag(IPointer pointer)
    {
        if (_pendingPaneId is null || !_placements.TryGetValue(_pendingPaneId, out var placement))
        {
            return;
        }

        _activePaneId = _pendingPaneId;
        _activePlacement = placement;
        _dragKind = _pendingKind;
        _resizeEdge = _pendingEdge;
        _hasMoved = true;
        _capturedPointer = pointer;
        pointer.Capture(this);

        if (_childByPaneId.TryGetValue(_activePaneId, out var child))
        {
            // 拖动中的窗格浮到最上层并轻微透明，明确“正在移动”的反馈。
            child.Opacity = 0.92;
            child.ZIndex = DraggingZIndex;
        }

        Cursor = new Cursor(FreeformInteractionLogic.MapResizeCursor(_resizeEdge, _dragKind == DragKind.Move));
        _overlay.SetActiveRect(_originRect);
        _overlay.SetHud("拖动中", new Point(_pressPoint.X + 16, _pressPoint.Y + 20));
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_dragKind == DragKind.None)
        {
            return;
        }

        var committed = _hasMoved;
        var paneId = _activePaneId;

        _dragKind = DragKind.None;
        _activePaneId = null;
        _activePlacement = null;
        _resizeEdge = ResizeEdge.None;
        _hasMoved = false;
        ClearPending();

        if (paneId is not null && _childByPaneId.TryGetValue(paneId, out var child))
        {
            child.Opacity = 1.0;
        }

        ApplyZIndexes();
        _overlay.SetGuides(null);
        _overlay.SetActiveRect(null);
        _overlay.SetHud(null, default);
        (pointer ?? _capturedPointer)?.Capture(null);
        _capturedPointer = null;
        InvalidateArrange();

        if (committed)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearPending()
    {
        _pendingPaneId = null;
        _pendingKind = DragKind.None;
        _pendingEdge = ResizeEdge.None;
    }

    private ResizeEdge HitTestResizeEdge(FreeformTilePlacement placement, Point position) =>
        FreeformInteractionLogic.HitTestResizeEdge(
            placement.ToRect(),
            Bounds.Width,
            Bounds.Height,
            position.X,
            position.Y);

    private string? FindTopmostPaneAt(Point position)
    {
        var placement = FreeformPaneHitTest.FindTopmost(
            _placements.Values,
            VisualOrderOf,
            Bounds.Width,
            Bounds.Height,
            position.X,
            position.Y);
        return placement?.PaneId;
    }

    /// <summary>子项在画布 Children 中的加入顺序（越大越晚加入，渲染时越靠上）。</summary>
    private int VisualOrderOf(string paneId) =>
        _childByPaneId.TryGetValue(paneId, out var child) ? Children.IndexOf(child) : -1;

    /// <summary>把窗格 ZIndex 归一化为唯一序列并同步到子项。</summary>
    private void NormalizeZIndexes()
    {
        FreeformPaneHitTest.NormalizeZIndexes(_placements.Values, VisualOrderOf);
        ApplyZIndexes();
    }

    /// <summary>把指定窗格提到所在层级组的最上层。</summary>
    public void BringToFront(string paneId)
    {
        if (!_placements.TryGetValue(paneId, out var placement))
        {
            return;
        }

        var pinned = placement.IsPinned;
        var maxZ = _placements.Values
            .Where(item => item.IsPinned == pinned && !string.Equals(item.PaneId, paneId, StringComparison.Ordinal))
            .Select(item => item.ZIndex)
            .DefaultIfEmpty(pinned ? 0 : 1)
            .Max();
        placement.ZIndex = pinned ? Math.Min(PinnedZMax - (int)PinnedZBase, maxZ + 1) : Math.Min(NormalZMax, maxZ + 1);
        NormalizeZIndexes();
    }

    private void SendToBack(string paneId)
    {
        if (!_placements.TryGetValue(paneId, out var placement))
        {
            return;
        }

        var pinned = placement.IsPinned;
        var minZ = _placements.Values
            .Where(item => item.IsPinned == pinned && !string.Equals(item.PaneId, paneId, StringComparison.Ordinal))
            .Select(item => item.ZIndex)
            .DefaultIfEmpty(1)
            .Min();
        placement.ZIndex = Math.Max(1, minZ - 1);
        NormalizeZIndexes();
    }

    private void ApplyZIndexes()
    {
        foreach (var (paneId, child) in _childByPaneId)
        {
            if (_placements.TryGetValue(paneId, out var placement))
            {
                child.ZIndex = placement.EffectiveZIndex;
            }
        }

        InvalidateVisual();
    }

    private void ShowTileMenu(string paneId, Point position)
    {
        var placement = _placements[paneId];
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.Pointer
        };
        menu.Items.Add(CreateMenuItem(
            placement.IsPinned ? "取消置顶（退出画中画）" : "📌 置顶（画中画）",
            () => TogglePinned(paneId)));
        menu.Items.Add(CreateMenuItem(
            placement.IsLocked ? "解锁位置" : "🔒 锁定位置",
            () => ToggleLocked(paneId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("置于顶层", () => BringToFront(paneId)));
        menu.Items.Add(CreateMenuItem("置于底层", () => SendToBack(paneId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("贴合 16:9", () => FitTo16By9(paneId)));
        menu.Open(this);
    }

    private MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void TogglePinned(string paneId)
    {
        var placement = _placements[paneId];
        placement.IsPinned = !placement.IsPinned;
        placement.ZIndex = placement.IsPinned ? 1 : Math.Min(NormalZMax, placement.ZIndex);
        NormalizeZIndexes();
        InvalidateArrange();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleLocked(string paneId)
    {
        var placement = _placements[paneId];
        placement.IsLocked = !placement.IsLocked;
        InvalidateArrange();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FitTo16By9(string paneId)
    {
        var height = Bounds.Height;
        if (height <= 0)
        {
            return;
        }

        var placement = _placements[paneId];
        var fitted = FreeformInteractionLogic.FitTo16By9(placement.ToRect(), Bounds.Width, height);
        placement.Top = fitted.Top;
        placement.Height = fitted.Height;

        InvalidateArrange();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCursor(Point position)
    {
        var paneId = FindTopmostPaneAt(position);
        if (paneId is null || !_placements.TryGetValue(paneId, out var placement))
        {
            Cursor = Cursor.Default;
            return;
        }

        if (placement.IsLocked)
        {
            Cursor = Cursor.Default;
            return;
        }

        var edge = FreeformInteractionLogic.HitTestResizeEdge(
            placement.ToRect(),
            Bounds.Width,
            Bounds.Height,
            position.X,
            position.Y);
        Cursor = new Cursor(FreeformInteractionLogic.MapResizeCursor(edge, edge == ResizeEdge.None));
    }

    private bool IsInteractiveChildSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Avalonia.Controls.Button
                or Avalonia.Controls.Primitives.ToggleButton
                or Avalonia.Controls.TextBox
                or Avalonia.Controls.ComboBox
                or Avalonia.Controls.Slider
                or Avalonia.Controls.Primitives.ScrollBar
                or Avalonia.Controls.MenuItem)
            {
                return true;
            }

            if (ReferenceEquals(current, this))
            {
                return false;
            }
        }

        return false;
    }

    private void OnDropDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(RoomIdFormat))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        var rect = ComputeDropRect(e.GetPosition(this));
        _overlay.SetActiveRect(rect);
        _overlay.SetHud("松开放入工作区", new Point(e.GetPosition(this).X + 16, e.GetPosition(this).Y + 20));
        e.Handled = true;
    }

    private void OnDropDragLeave(object? sender, RoutedEventArgs e)
    {
        _overlay.SetActiveRect(null);
        _overlay.SetHud(null, default);
    }

    private void OnDropDrop(object? sender, DragEventArgs e)
    {
        _overlay.SetActiveRect(null);
        _overlay.SetHud(null, default);

        if (!e.DataTransfer.Contains(RoomIdFormat)
            || !Guid.TryParse(e.DataTransfer.TryGetValue(RoomIdFormat), out var roomId))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        RoomDropped?.Invoke(this, new RoomDropEventArgs(roomId, ComputeDropRect(e.GetPosition(this))));
    }

    private NormalizedRect ComputeDropRect(Point position) =>
        FreeformInteractionLogic.ComputeDropRect(position.X, position.Y, Bounds.Width, Bounds.Height);
}

/// <summary>房间从左侧列表拖放到自由画布时的事件参数。</summary>
public sealed class RoomDropEventArgs : EventArgs
{
    public RoomDropEventArgs(Guid roomId, NormalizedRect rect)
    {
        RoomId = roomId;
        Rect = rect;
    }

    public Guid RoomId { get; }

    public NormalizedRect Rect { get; }
}
