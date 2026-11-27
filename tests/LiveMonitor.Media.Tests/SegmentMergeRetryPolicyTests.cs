using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// <see cref="SegmentMergeRetryPolicy"/> 的纯逻辑单测：只在首次失败且仍有多个分片时，
/// 返回剔除末尾分片后的重试列表；其余情况不再重试。
/// </summary>
public sealed class SegmentMergeRetryPolicyTests
{
    private static readonly RecordedSegment[] ThreeSegments =
    {
        new("part-000.mkv", TimeSpan.FromSeconds(1), null),
        new("part-001.mkv", TimeSpan.FromSeconds(1), null),
        new("part-002.mkv", TimeSpan.FromSeconds(1), null),
    };

    [Fact]
    public void NextAttempt_DropsLastSegmentOnFirstFailure()
    {
        var next = SegmentMergeRetryPolicy.NextAttempt(ThreeSegments, tailRetries: 0);

        Assert.NotNull(next);
        Assert.Equal(new[] { "part-000.mkv", "part-001.mkv" }, next!.Select(segment => segment.Path));
    }

    [Fact]
    public void NextAttempt_StopsAfterConfiguredRetries()
    {
        Assert.Null(SegmentMergeRetryPolicy.NextAttempt(ThreeSegments, tailRetries: 1));
    }

    [Fact]
    public void NextAttempt_StopsWhenOnlyOneSegmentRemains()
    {
        Assert.Null(SegmentMergeRetryPolicy.NextAttempt(new[] { ThreeSegments[0] }, tailRetries: 0));
    }

    [Fact]
    public void NextAttempt_NullSegments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SegmentMergeRetryPolicy.NextAttempt(null!, tailRetries: 0));
    }
}
