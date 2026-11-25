using CommunityToolkit.Mvvm.ComponentModel;
using LiveMonitor.Desktop.Services;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 主窗口底部状态栏的只读子视图模型：状态文案、独轮车拦截数、主库/工作区计数、
/// 系统指标（内存 / GPU / 解码 / 编码）与解码健康摘要。
/// 对外只暴露 getter；唯一写入通道是 internal 更新方法，由 <see cref="MainWindowViewModel"/>
/// 在各数据源更新处调用（工作区计数、健康采样、弹幕拦截数等）。
/// 线程 / Dispatcher 时序与抽离前完全一致：所有更新都在 UI 线程执行；后台数据源由主 VM
/// 先 Post 到 UI 线程再写入，本类不自行切换线程。
/// </summary>
public sealed class SystemStatusBarViewModel : ObservableObject, IDisposable
{
    private readonly SystemMetricsService _metrics = new();
    private readonly HealthSummaryCoordinator _healthCoordinator = new();

    private string _statusText = "就绪";
    private string _unicycleBlockedText = WorkspaceLayoutCoordinator.FormatUnicycleBlockedText(0);
    private string _roomCountText = string.Empty;
    private string _healthText = "解码正常 (0% 丢帧)";
    private bool _isHealthWarning;
    private bool _disposed;

    /// <summary>状态栏左侧文案（「就绪」「已移出 xxx」等）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>V0.2：由 DanmakuManager 推送的累计被拦截用户数文案。</summary>
    public string UnicycleBlockedText
    {
        get => _unicycleBlockedText;
        private set => SetProperty(ref _unicycleBlockedText, value);
    }

    /// <summary>主库 / 工作区计数文案。</summary>
    public string RoomCountText
    {
        get => _roomCountText;
        private set => SetProperty(ref _roomCountText, value);
    }

    /// <summary>解码健康摘要文案（顶栏健康胶囊与状态栏共用同一份数据）。</summary>
    public string HealthText
    {
        get => _healthText;
        private set => SetProperty(ref _healthText, value);
    }

    /// <summary>true 表示当前丢帧率超过告警阈值。</summary>
    public bool IsHealthWarning
    {
        get => _isHealthWarning;
        private set => SetProperty(ref _isHealthWarning, value);
    }

    /// <summary>内存 / GPU 指标，由视图在窗口打开时调用 <see cref="SystemMetricsService.Start"/>。</summary>
    public SystemMetricsService Metrics => _metrics;

    /// <summary>更新状态栏文案；返回值表示文案是否变化（主 VM 据此决定是否弹 Toast）。</summary>
    internal bool UpdateStatusText(string value)
    {
        if (string.Equals(_statusText, value, StringComparison.Ordinal))
        {
            return false;
        }

        StatusText = value;
        return true;
    }

    /// <summary>更新「独轮车已拦」用户数：负数按 0 处理，文案不变时不发通知。</summary>
    internal void UpdateUnicycleBlockedUserCount(int blockedUserCount)
    {
        UnicycleBlockedText = WorkspaceLayoutCoordinator.FormatUnicycleBlockedText(Math.Max(0, blockedUserCount));
    }

    /// <summary>更新主库 / 工作区计数文案。</summary>
    internal void UpdateWorkspaceSummary(
        int libraryCount,
        bool isHeroLayout,
        int workspaceRoomCount,
        int gridRows,
        int gridColumns)
    {
        RoomCountText = WorkspaceLayoutCoordinator.FormatRoomCountText(
            libraryCount,
            isHeroLayout,
            workspaceRoomCount,
            gridRows,
            gridColumns);
    }

    /// <summary>
    /// 用一轮窗格采样刷新解码健康摘要（与抽离前 RefreshHealth 的顺序一致：
    /// 先 IsHealthWarning 再 HealthText，两处变更仍在调用线程上触发）。
    /// </summary>
    internal void UpdateHealth(IEnumerable<PaneHealthSample> samples)
    {
        var summary = _healthCoordinator.Update(samples);
        IsHealthWarning = summary.IsWarning;
        HealthText = summary.Text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metrics.Dispose();
    }
}
