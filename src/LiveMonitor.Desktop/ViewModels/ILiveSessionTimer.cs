namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 「边录边剪」自动同步定时器抽象：生产环境使用 Avalonia UI 线程 <c>DispatcherTimer</c>，
/// 单元测试使用可手动触发 tick 的 Fake，从而断言同步周期、重入与停止行为。
/// </summary>
internal interface ILiveSessionTimer
{
    /// <summary>按指定间隔启动（或更新间隔后启动）定时器；重复调用保持幂等。</summary>
    void Start(TimeSpan interval, Func<Task> tick);

    /// <summary>停止定时器；未启动时静默忽略。</summary>
    void Stop();
}
