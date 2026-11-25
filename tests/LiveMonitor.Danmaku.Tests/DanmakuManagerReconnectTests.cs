using LiveMonitor.Core.Enums;
using Xunit;

namespace LiveMonitor.Danmaku.Tests;

/// <summary>
/// 补充 DanmakuManager 的重连细节：Disconnected 后按 3/10/30/60/60 秒退避调度，
/// 以及移除房间时与「退避等待中」的重连竞态（取消等待后不再重连、后台循环干净退出）。
/// </summary>
public sealed class DanmakuManagerReconnectTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Disconnected_UsesBackoffSequence_3s10s30s60s60s()
    {
        await using var harness = new DanmakuManagerHarness();
        await harness.InitializeAsync();
        using var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");

        var expected = new[]
        {
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60),
        };

        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            // 每次断线后连接循环都会重建客户端，这里逐次驱动断开并校验退避时长。
            var client = await harness.WaitForClientAsync(index: attempt);
            client.RaiseDisconnected(new IOException($"第 {attempt + 1} 次断线"));

            var expectedCount = attempt + 1;
            await TestWait.WaitUntilAsync(
                () => harness.Delay.BackoffWaits.Count >= expectedCount,
                message: $"第 {expectedCount} 次断线后的退避等待");

            Assert.Equal(expected[attempt], harness.Delay.BackoffWaits[attempt]);
        }

        // 退避达到上限后固定 60 秒，且日志按「N 秒后重连」记录。
        foreach (var seconds in new[] { 3, 10, 30, 60 })
        {
            Assert.Contains(harness.Logs, log => log.Contains($"{seconds} 秒后重连", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ReleaseHandle_DuringReconnectWait_StopsReconnecting_AndLeavesNoPendingTask()
    {
        await using var harness = new DanmakuManagerHarness();
        harness.Delay.BlockBackoff = true;
        await harness.InitializeAsync();

        var handle = harness.Manager.Acquire(PlatformType.Bilibili, "1001");
        var firstClient = await harness.WaitForClientAsync(index: 0);

        // Disconnected(null) 表示对端正常关闭，同样应触发重连退避。
        firstClient.RaiseDisconnected(null);
        await TestWait.WaitUntilAsync(
            () => harness.Delay.BackoffWaits.Count >= 1,
            message: "断线后进入退避等待");
        Assert.Equal(TimeSpan.FromSeconds(3), harness.Delay.BackoffWaits[0]);

        // 最后一个引用在退避等待期间被移除：取消等待、退出循环，且不再创建新连接。
        handle.Dispose();

        await TestWait.WaitUntilAsync(
            () => harness.Logs.Any(message => message.Contains("连接循环已退出", StringComparison.Ordinal)),
            message: "释放引用后后台连接循环退出");

        Assert.Equal(1, harness.Factory.CreateCount);
        Assert.Equal(1, firstClient.DisconnectCount);
        Assert.Equal(1, firstClient.DisposeCount);

        // 等待中不残留无法结束的后台任务：Manager 释放应快速完成。
        await harness.Manager.DisposeAsync().AsTask().WaitAsync(ShortWait);
    }
}
