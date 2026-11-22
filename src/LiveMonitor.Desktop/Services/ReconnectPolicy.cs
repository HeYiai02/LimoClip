namespace LiveMonitor.Desktop.Services;

/// <summary>断流重连退避：3s、10s、30s，之后保持 30s。</summary>
public static class ReconnectPolicy
{
    public static TimeSpan DelayForAttempt(int attempt)
        => TimeSpan.FromSeconds(attempt switch
        {
            <= 1 => 3,
            2 => 10,
            _ => 30
        });
}
