using System.Diagnostics;
using Avalonia.Threading;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕滚动共享帧循环：所有 <see cref="DanmakuOverlay"/> 复用同一个 60fps 定时器。
/// 之前每个窗格各起一个 30fps 的 DispatcherTimer，多屏时定时器数量随窗格数线性增长；
/// 改成共享 60fps 后既减少调度开销，也让弹幕滚动更顺滑（30fps 在 60Hz 屏幕上是明显的跳帧感）。
/// </summary>
internal static class DanmakuRenderTicker
{
    private const int FrameIntervalMilliseconds = 16;
    private static readonly List<WeakReference<DanmakuOverlay>> Overlays = new();
    private static DispatcherTimer? _timer;
    private static long _lastTimestamp;

    public static void Register(DanmakuOverlay overlay)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Register(overlay));
            return;
        }

        for (var i = Overlays.Count - 1; i >= 0; i--)
        {
            if (!Overlays[i].TryGetTarget(out var existing))
            {
                Overlays.RemoveAt(i);
            }
            else if (ReferenceEquals(existing, overlay))
            {
                EnsureTimer();
                return;
            }
        }

        Overlays.Add(new WeakReference<DanmakuOverlay>(overlay));
        EnsureTimer();
    }

    public static void Unregister(DanmakuOverlay overlay)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Unregister(overlay));
            return;
        }

        for (var i = Overlays.Count - 1; i >= 0; i--)
        {
            if (!Overlays[i].TryGetTarget(out var existing) || ReferenceEquals(existing, overlay))
            {
                Overlays.RemoveAt(i);
            }
        }

        if (Overlays.Count == 0)
        {
            StopTimer();
        }
    }

    private static void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _lastTimestamp = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameIntervalMilliseconds) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _lastTimestamp = 0;
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var delta = _lastTimestamp == 0
            ? FrameIntervalMilliseconds / 1000.0
            : Math.Clamp((now - _lastTimestamp) / (double)Stopwatch.Frequency, 0.005, 0.1);
        _lastTimestamp = now;

        for (var i = Overlays.Count - 1; i >= 0; i--)
        {
            if (!Overlays[i].TryGetTarget(out var overlay))
            {
                Overlays.RemoveAt(i);
                continue;
            }

            if (!overlay.AdvanceFrame(delta))
            {
                Overlays.RemoveAt(i);
            }
        }

        if (Overlays.Count == 0)
        {
            StopTimer();
        }
    }
}
