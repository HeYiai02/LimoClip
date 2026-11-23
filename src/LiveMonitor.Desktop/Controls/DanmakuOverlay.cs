using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕渲染层：只负责把已通过过滤的弹幕画出来，不持有连接、不做统计。
/// 上层（DanmakuManager）可以按 50ms 批量推送；本控件由共享的 60fps 帧循环推进
/// （<see cref="DanmakuRenderTicker"/>，多屏时共用一个定时器），
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
    private const double EmoteImageHeight = 22;

    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#F2F5FA"));
    private static readonly IBrush GiftBrush = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.Parse("#CC000000"));
    private static readonly Typeface OverlayTypeface = new(FontFamily.Default);

    private sealed class Segment
    {
        public FormattedText? Text { get; init; }
        public FormattedText? Shadow { get; init; }
        public Bitmap? Image { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
    }

    private sealed class Item
    {
        public required IReadOnlyList<Segment> Segments { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public double X { get; set; }
        public double Speed { get; init; }
        public int Lane { get; init; }
    }

    private readonly List<Item> _items = new();
    private readonly Queue<DanmakuMessage> _pending = new();
    private readonly Random _random = new();
    private int _laneCount = 1;
    private double _laneHeight = LaneHeight;
    private int _maxConcurrent = 30;
    private bool _blocked;
    private bool _paused;
    private double _playbackRate = 1.0;

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

    /// <summary>
    /// 暂停推进（本地剪辑预览暂停时用）。暂停期间已在屏弹幕冻结、不再从待渲染队列
    /// 出生；恢复后继续按原速度移动。默认 false，直播路径行为不变。
    /// </summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>
    /// 弹幕移动倍率。本地剪辑 2 倍速播放时设为 2，让弹幕速度和画面保持一致；
    /// 允许 0.25~4 以防异常值把速度算飞。默认 1.0，直播路径行为不变。
    /// </summary>
    public double PlaybackRate
    {
        get => _playbackRate;
        set => _playbackRate = double.IsFinite(value) ? Math.Clamp(value, 0.25, 4.0) : 1.0;
    }

    /// <summary>
    /// 是否渲染纯大表情包弹幕（V0.2 只显示 [表情名] 文本）。
    /// 关闭后这类弹幕仍参与高能统计，只是不进入渲染队列，避免多屏下弹幕海挤占轨道。
    /// </summary>
    public bool ShowStickers { get; set; } = true;

    /// <summary>
    /// 是否把表情渲染成静态图片（V0.2 可选，默认关闭）。
    /// 关闭时行内表情显示 <c>[doge]</c> 文本、纯大表情显示 <c>[表情名]</c>；
    /// 开启后优先使用图片，未加载成功的表情仍退回文本，不阻塞弹幕出现。
    /// 多屏时建议保持关闭：图片解码与缓存会额外占用 UI 线程和内存。
    /// </summary>
    public bool ShowEmoteImages { get; set; }

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
        DanmakuRenderTicker.Unregister(this);
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
                    DanmakuRenderTicker.Register(this);
                }
            }
            else
            {
                _blocked = true;
                Clear();
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DanmakuRenderTicker.Unregister(this);
    }

    private void EnqueueCore(DanmakuMessage message)
    {
        if (_blocked || !IsVisible || string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        // 纯表情包按设置降级：只保留统计与高能检测，不占渲染轨道。
        if (!ShowStickers && message.IsSticker)
        {
            return;
        }

        if (_pending.Count >= MaxPending)
        {
            // 渲染端压力过大时丢弃最旧待渲染项，不阻塞上游。
            _pending.Dequeue();
        }

        _pending.Enqueue(message);
        DanmakuRenderTicker.Register(this);
    }

    /// <summary>
    /// 由 <see cref="DanmakuRenderTicker"/> 的共享帧循环调用，推进一帧；
    /// 返回 false 表示当前没有需要继续渲染的内容（帧循环会把本控件移出列表）。
    /// </summary>
    internal bool AdvanceFrame(double delta)
    {
        if (_blocked || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            // 布局还没就绪/被隐藏时保留注册，等下一帧再试。
            return _pending.Count > 0;
        }

        // 暂停时冻结屏上弹幕并暂缓出生，保持注册等恢复后继续。
        if (_paused)
        {
            return _items.Count > 0 || _pending.Count > 0;
        }

        delta *= PlaybackRate;

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
            return false;
        }

        InvalidateVisual();
        return true;
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
            var text = Normalize(FormatText(message));
            if (text.Length == 0)
            {
                continue;
            }

            var brush = message.IsGiftOrSuperChat ? GiftBrush : NormalBrush;
            var segments = BuildSegments(message, text, brush);
            if (segments.Count == 0)
            {
                continue;
            }

            var totalWidth = 0d;
            var itemHeight = 0d;
            for (var i = 0; i < segments.Count; i++)
            {
                totalWidth += segments[i].Width;
                itemHeight = Math.Max(itemHeight, segments[i].Height);
            }

            if (totalWidth <= 0)
            {
                continue;
            }

            var duration = MinDurationSeconds + _random.NextDouble() * (MaxDurationSeconds - MinDurationSeconds);
            _items.Add(new Item
            {
                Segments = segments,
                Width = totalWidth,
                Height = itemHeight,
                X = width + 4,
                Speed = (width + totalWidth) / duration,
                Lane = lane
            });
        }
    }

    private int FindFreeLane(double width)
    {
        // 逐条尝试：轨道里最后一条弹幕完全进场后才允许下一条，避免重叠。
        // 从最上方开始找第一条空闲轨道，避免轮转分配在弹幕稀疏时只占下半屏。
        return DanmakuLaneAllocator.FindFreeLane(_laneCount, GetLastItemRightEdge, width, SpawnGap);
    }

    private double GetLastItemRightEdge(int lane)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.Lane == lane)
            {
                return item.X + item.Width;
            }
        }

        return double.NegativeInfinity;
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
            var x = item.X;
            foreach (var segment in item.Segments)
            {
                if (segment.Image is { } image)
                {
                    var imageTop = y + (item.Height - segment.Height) / 2;
                    context.DrawImage(image, new Rect(x, imageTop, segment.Width, segment.Height));
                }
                else if (segment.Text is { } text && segment.Shadow is { } shadow)
                {
                    context.DrawText(shadow, new Point(x + 1.2, y + 1.2));
                    context.DrawText(text, new Point(x, y));
                }

                x += segment.Width;
            }
        }
    }

    /// <summary>
    /// 构建一条弹幕的绘制分段：默认整条按文本；开启表情图片后，
    /// 纯大表情包优先画图片、行内表情按 <c>[表情名]</c> 匹配图片地址，
    /// 未命中缓存的表情保留文本并触发后台下载。
    /// </summary>
    private IReadOnlyList<Segment> BuildSegments(DanmakuMessage message, string text, IBrush brush)
    {
        if (ShowEmoteImages)
        {
            if (message.IsSticker)
            {
                var stickerUrl = message.Emotes is { Count: > 0 } ? message.Emotes[0].Url : null;
                if (!string.IsNullOrEmpty(stickerUrl))
                {
                    var sticker = TryGetEmoteImage(stickerUrl);
                    if (sticker is not null)
                    {
                        return new[] { sticker };
                    }
                }
            }
            else if (text.IndexOf('[') >= 0)
            {
                var segments = BuildInlineSegments(message.Emotes, text, brush);
                if (segments.Count > 0)
                {
                    return segments;
                }
            }
        }

        return new[] { CreateTextSegment(text, brush) };
    }

    private static IReadOnlyList<Segment> BuildInlineSegments(
        IReadOnlyList<DanmakuEmote>? emotes,
        string text,
        IBrush brush)
    {
        var segments = new List<Segment>();
        var pending = new System.Text.StringBuilder();

        void FlushText()
        {
            if (pending.Length == 0)
            {
                return;
            }

            segments.Add(CreateTextSegment(pending.ToString(), brush));
            pending.Clear();
        }

        foreach (var part in DanmakuEmoteText.Split(text))
        {
            if (part.Text is { } plain)
            {
                pending.Append(plain);
                continue;
            }

            if (part.EmoteName is not { } emoteName)
            {
                continue;
            }

            var url = DanmakuEmoteText.ResolveImageUrl(emotes, emoteName);
            var image = string.IsNullOrEmpty(url) ? null : TryGetEmoteImage(url);
            if (image is not null)
            {
                FlushText();
                segments.Add(image);
            }
            else
            {
                // 未命中缓存/没有图片地址：先显示 [表情名] 文本，后续弹幕会命中缓存。
                pending.Append('[').Append(emoteName).Append(']');
            }
        }

        FlushText();
        return segments;
    }

    private static Segment? TryGetEmoteImage(string url)
    {
        if (DanmakuEmoteImageCache.TryGet(url, out var bitmap) && bitmap is not null)
        {
            var ratio = bitmap.PixelSize.Height > 0
                ? EmoteImageHeight / bitmap.PixelSize.Height
                : 1;
            return new Segment
            {
                Image = bitmap,
                Width = Math.Max(1, bitmap.PixelSize.Width * ratio),
                Height = EmoteImageHeight
            };
        }

        DanmakuEmoteImageCache.Request(url);
        return null;
    }

    private static Segment CreateTextSegment(string text, IBrush brush)
    {
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
        return new Segment
        {
            Text = formatted,
            Shadow = shadow,
            Width = formatted.Width,
            Height = formatted.Height
        };
    }

    /// <summary>
    /// 渲染文本格式化：纯大表情包显示为 <c>[表情名]</c>（V0.2 不下载图片），
    /// 行内小表情保留原始 <c>[doge]</c> 文本。V0.4 导出 .ass 时同样走这一段文本。
    /// </summary>
    private static string FormatText(DanmakuMessage message)
    {
        if (!message.IsSticker)
        {
            return message.Content;
        }

        var name = message.PrimaryEmoteName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = message.Content;
        }

        name = name.Trim().Trim('[', ']').Trim();
        return name.Length == 0 ? message.Content : $"[{name}]";
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
