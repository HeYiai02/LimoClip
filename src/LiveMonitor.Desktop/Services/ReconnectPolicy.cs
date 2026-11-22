namespace LiveMonitor.Desktop.Services;

/// <summary>断流重连退避：3s、10s、30s；超过 3 次后进入 60s 失败复检。</summary>
public static class ReconnectPolicy
{
    public const int MaxAttempts = 3;

    public static TimeSpan FailureRecheckDelay => TimeSpan.FromSeconds(60);

    public static TimeSpan DelayForAttempt(int attempt)
        => TimeSpan.FromSeconds(attempt switch
        {
            <= 1 => 3,
            2 => 10,
            _ => 30
        });
}
