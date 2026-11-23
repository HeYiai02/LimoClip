using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LiveMonitor.Core.Models;
using Control = Avalonia.Controls.Control;
using Point = Avalonia.Point;
using DrawingContext = Avalonia.Media.DrawingContext;
using FormattedText = Avalonia.Media.FormattedText;
using IBrush = Avalonia.Media.IBrush;
using SolidColorBrush = Avalonia.Media.SolidColorBrush;
using Color = Avalonia.Media.Color;
using FontFamily = Avalonia.Media.FontFamily;
using Typeface = Avalonia.Media.Typeface;
using FlowDirection = Avalonia.Media.FlowDirection;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕渲染层：只负责把已通过过滤的弹幕画出来，不持有连接、不做统计。
/// 上层（DanmakuManager）可以按 50ms 批量推送；本控件用 30fps 定时器推进，
/// 轨道/同屏容量满了直接丢弃渲染（统计与高能检测不受影响）。
/// 控件本身 IsHitTestVisible=false，不会吃掉窗格的双击与拖拽。
/// </summary>
public sealed class DanmakuOverlay : Control
{
    private const double FontSize = 17;
    private const double LaneHeight = 26;
    private const double LaneTopPadding = 6;
    private const double SpawnGap = 24;
    private const int MaxPending = 600;
    private const double MinDurationSeconds = 7;
    private const double MaxDurationSeconds = 11;

    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#F2F5FA"));
    private static readonly IBrush GiftBrush = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.Parse("#CC000000"));
    private static readonly Typeface OverlayTypeface = new(FontFamily.Default);

    private sealed class Item
    {
        public required FormattedText Text { get; init; }
        public required FormattedText Shadow { get; init; }
        public required double Width { get; init; }
        public double X { get; set; }
        public double Speed { get; init; }
        public int Lane { get; init; }
    }

    private readonly List<Item> _items = new();
    private readonly Queue<DanmakuMessage> _pending = new();
    private readonly Random _random = new();
    private DispatcherTimer? _timer;
    private long _lastTickTimestamp;
    private int _laneCount = 1;
    private double _laneHeight = LaneHeight;
    private int _nextLane;
    private int _maxConcurrent = 30;
    private bool _blocked;

    public DanmakuOverlay()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    /// <summary>同屏最多保留多少条；窄窗（紧凑模式）由播放器调小。</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set => _maxConcurrent = Math.Clamp(value, 1, 200);
    }

    /// <summary>当前屏幕上的弹幕数（仅诊断用）。</summary>
    public int ActiveCount => _items.Count;

    /// <summary>把一条弹幕加入渲染队列；非 UI 线程会自动切回 UI 线程。</summary>
    public void Enqueue(DanmakuMessage message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => EnqueueCore(message));
            return;
        }

        EnqueueCore(message);
    }

    /// <summary>批量入队，减少跨线程调度次数。</summary>
    public void EnqueueRange(IReadOnlyList<DanmakuMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    EnqueueCore(messages[i]);
                }
            });
            return;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            EnqueueCore(messages[i]);
        }
    }

    /// <summary>清空当前渲染内容（切换弹幕关/开、方案重建时用）。</summary>
    public void Clear()
    {
        _items.Clear();
        _pending.Clear();
        StopTimer();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _blocked = false;
                if (_pending.Count > 0)
                {
                    EnsureTimer();
                }
            }
            else
            {
                _blocked = true;
                Clear();
            }
        }
    }

    private void EnqueueCore(DanmakuMessage message)
    {
        if (_blocked || !IsVisible || string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        if (_pending.Count >= MaxPending)
        {
            // 渲染端压力过大时丢弃最旧待渲染项，不阻塞上游。
            _pending.Dequeue();
        }

        _pending.Enqueue(message);
        EnsureTimer();
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _lastTickTimestamp = 0;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _lastTickTimestamp = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            StopTimer();
            return;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var delta = _lastTickTimestamp == 0
            ? 0.033
            : Math.Clamp((nowTicks - _lastTickTimestamp) / (double)Stopwatch.Frequency, 0.005, 0.12);
        _lastTickTimestamp = nowTicks;

        var height = Bounds.Height;
        var laneCount = Math.Max(1, (int)((height - LaneTopPadding * 2) / LaneHeight));
        if (laneCount != _laneCount)
        {
            _laneCount = laneCount;
            _laneHeight = Math.Max(LaneHeight, (height - LaneTopPadding * 2) / laneCount);
            _items.RemoveAll(item => item.Lane >= laneCount);
        }

        // 推进 + 回收离屏项。
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            item.X -= item.Speed * delta;
            if (item.X + item.Width < 0)
            {
                _items.RemoveAt(i);
            }
        }

        SpawnPending();

        if (_items.Count == 0 && _pending.Count == 0)
        {
            StopTimer();
        }

        InvalidateVisual();
    }

    private void SpawnPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var width = Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        while (_pending.Count > 0 && _items.Count < MaxConcurrent)
        {
            var lane = FindFreeLane(width);
            if (lane < 0)
            {
                return;
            }

            var message = _pending.Dequeue();
            var text = Normalize(message.Content);
            if (text.Length == 0)
            {
                continue;
            }

            var brush = message.IsGiftOrSuperChat ? GiftBrush : NormalBrush;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                OverlayTypeface,
                FontSize,
                brush);
            var shadow = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                OverlayTypeface,
                FontSize,
                ShadowBrush);

            var duration = MinDurationSeconds + _random.NextDouble() * (MaxDurationSeconds - MinDurationSeconds);
            _items.Add(new Item
            {
                Text = formatted,
                Shadow = shadow,
                Width = formatted.Width,
                X = width + 4,
                Speed = (width + formatted.Width) / duration,
                Lane = lane
            });
        }
    }

    private int FindFreeLane(double width)
    {
        // 逐条尝试：轨道里最后一条弹幕完全进场后才允许下一条，避免重叠。
        for (var step = 0; step < _laneCount; step++)
        {
            var lane = (_nextLane + step) % _laneCount;
            var free = true;
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.Lane != lane)
                {
                    continue;
                }

                if (item.X + item.Width > width - SpawnGap)
                {
                    free = false;
                }

                break;
            }

            if (free)
            {
                _nextLane = (lane + 1) % _laneCount;
                return lane;
            }
        }

        return -1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_items.Count == 0)
        {
            return;
        }

        var topPadding = LaneTopPadding;
        foreach (var item in _items)
        {
            var y = topPadding + item.Lane * _laneHeight;
            var point = new Point(item.X, y);
            var shadowPoint = new Point(item.X + 1.2, y + 1.2);
            context.DrawText(item.Shadow, shadowPoint);
            context.DrawText(item.Text, point);
        }
    }

    private static string Normalize(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            trimmed = trimmed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        // 超长弹幕只渲染前 120 个字符，避免一条弹幕横跨整个窗格。
        return trimmed.Length <= 120 ? trimmed : trimmed.Substring(0, 120) + "…";
    }
}
