using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class ReconnectPolicyTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 10)]
    [InlineData(3, 30)]
    [InlineData(10, 30)]
    public void DelayForAttempt_UsesThreeTenThirtyBackoff(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ReconnectPolicy.DelayForAttempt(attempt));
    }

    [Fact]
    public void RetryBudget_IsThreeAttemptsThen60SecondRecheck()
    {
        Assert.Equal(3, ReconnectPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectPolicy.FailureRecheckDelay);
    }
}
