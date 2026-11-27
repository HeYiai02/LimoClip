using Avalonia.Threading;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 基于 Avalonia UI 线程定时器的 <see cref="ILiveSessionTimer"/> 实现。
/// 单元测试 / 无 UI 平台线程时无法创建定时器，按既有约定静默降级（不抛异常）。
/// </summary>
internal sealed class DispatcherLiveSessionTimer : ILiveSessionTimer
{
    private DispatcherTimer? _timer;
    private Func<Task>? _tick;

    public void Start(TimeSpan interval, Func<Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        _tick = tick;
        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = interval };
                _timer.Tick += OnTick;
            }
            else
            {
                _timer.Interval = interval;
            }

            _timer.Start();
        }
        catch (Exception)
        {
            // 测试 / 无 Avalonia 平台线程时无法创建 UI 定时器，跳过自动同步。
        }
    }

    public void Stop()
    {
        try
        {
            _timer?.Stop();
        }
        catch (Exception)
        {
            // 释放路径忽略。
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_tick is { } tick)
        {
            _ = tick();
        }
    }
}
