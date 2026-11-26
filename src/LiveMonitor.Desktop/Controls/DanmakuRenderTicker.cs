using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕滚动共享帧循环：所有 <see cref="DanmakuOverlay"/> 复用同一个帧回调。
/// 之前用 16ms <see cref="DispatcherTimer"/> 驱动，定时器回调与画面刷新不同步
/// （UI 线程忙时会被推迟、多路同时重绘还会挤在同一帧），滚动观感会一顿一顿；
/// 现在改为 <see cref="TopLevel.RequestAnimationFrame"/>：每帧由合成器给时间戳，
/// 推进量与屏幕刷新对齐，多路弹幕都在同一 vsync 帧内更新，滚动更均匀。
/// 另带一个 1 秒低频看门狗：窗口最小化/合成器暂停后若帧回调丢失，自动重新排队，
/// 避免弹幕永久冻结。
/// </summary>
internal static class DanmakuRenderTicker
{
    private static readonly List<WeakReference<DanmakuOverlay>> Overlays = new();
    private static bool _running;
    private static TimeSpan? _lastTimestamp;
    private static long _lastFrameTick;
    private static DispatcherTimer? _watchdog;

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
                EnsureRunning(overlay);
                return;
            }
        }

        Overlays.Add(new WeakReference<DanmakuOverlay>(overlay));
        EnsureRunning(overlay);
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
            Stop();
        }
    }

    private static void EnsureRunning(DanmakuOverlay overlay)
    {
        if (_running)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(overlay);
        if (topLevel is null)
        {
            // 控件还没挂到窗口上：等 OnAttachedToVisualTree 再注册。
            return;
        }

        _running = true;
        _lastTimestamp = null;
        _lastFrameTick = Environment.TickCount64;
        RequestFrame(topLevel);
        EnsureWatchdog();
    }

    private static void RequestFrame(TopLevel topLevel)
    {
        topLevel.RequestAnimationFrame(timestamp => OnFrame(topLevel, timestamp));
    }

    private static void OnFrame(TopLevel topLevel, TimeSpan timestamp)
    {
        if (!_running)
        {
            return;
        }

        _lastFrameTick = Environment.TickCount64;

        var delta = DanmakuRenderTiming.ResolveFrameDelta(_lastTimestamp, timestamp);
        _lastTimestamp = timestamp;

        var hasActiveOverlay = false;
        for (var i = Overlays.Count - 1; i >= 0; i--)
        {
            if (!Overlays[i].TryGetTarget(out var overlay))
            {
                Overlays.RemoveAt(i);
                continue;
            }

            if (overlay.AdvanceFrame(delta))
            {
                hasActiveOverlay = true;
            }
            else
            {
                Overlays.RemoveAt(i);
            }
        }

        if (hasActiveOverlay && Overlays.Count > 0)
        {
            RequestFrame(topLevel);
        }
        else
        {
            Stop();
        }
    }

    private static void EnsureWatchdog()
    {
        if (_watchdog is not null)
        {
            return;
        }

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => CheckFrameStall();
        _watchdog.Start();
    }

    private static void CheckFrameStall()
    {
        if (!_running)
        {
            StopWatchdog();
            return;
        }

        if (!DanmakuRenderTiming.IsFrameStalled(_lastFrameTick, Environment.TickCount64))
        {
            return;
        }

        // 帧回调被丢弃（典型场景：窗口最小化、合成器暂停渲染）时重新排队；
        // 否则 _running 会一直为 true，新弹幕进来也无法驱动帧循环。
        _running = false;
        for (var i = Overlays.Count - 1; i >= 0; i--)
        {
            if (!Overlays[i].TryGetTarget(out var overlay))
            {
                Overlays.RemoveAt(i);
                continue;
            }

            if (TopLevel.GetTopLevel(overlay) is not null)
            {
                EnsureRunning(overlay);
                return;
            }

            Overlays.RemoveAt(i);
        }

        StopWatchdog();
    }

    private static void StopWatchdog()
    {
        if (_watchdog is null)
        {
            return;
        }

        _watchdog.Stop();
        _watchdog = null;
    }

    private static void Stop()
    {
        _running = false;
        _lastTimestamp = null;
        _lastFrameTick = 0;
        StopWatchdog();
    }
}
