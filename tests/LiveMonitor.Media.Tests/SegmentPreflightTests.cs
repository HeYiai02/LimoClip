using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// <see cref="SegmentPreflight"/> 的纯文件系统单测：空白路径/缺失/0 字节的分片被剔除并记录原因，
/// 可读的非空分片按原顺序保留。
/// </summary>
public sealed class SegmentPreflightTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests",
        "SegmentPreflight",
        Guid.NewGuid().ToString("N"));

    public SegmentPreflightTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Filter_RemovesBlankMissingAndZeroByteSegments()
    {
        var validFirst = Path.Combine(_directory, "part-000.mkv");
        var validSecond = Path.Combine(_directory, "part-001.mkv");
        File.WriteAllBytes(validFirst, new byte[] { 0x01, 0x02 });
        File.WriteAllBytes(validSecond, new byte[] { 0x03, 0x04 });
        var zeroByte = Path.Combine(_directory, "part-002.mkv");
        File.WriteAllBytes(zeroByte, Array.Empty<byte>());
        var missing = Path.Combine(_directory, "part-003.mkv");

        var (usable, skipped) = SegmentPreflight.Filter(new[]
        {
            new RecordedSegment(validFirst, TimeSpan.FromSeconds(1), null),
            new RecordedSegment("   ", TimeSpan.FromSeconds(1), null),
            new RecordedSegment(missing, TimeSpan.FromSeconds(1), null),
            new RecordedSegment(zeroByte, TimeSpan.FromSeconds(1), null),
            new RecordedSegment(validSecond, TimeSpan.FromSeconds(1), null),
        });

        Assert.Equal(new[] { validFirst, validSecond }, usable.Select(segment => segment.Path));
        Assert.Equal(3, skipped.Count);
        Assert.Contains("(空路径)", skipped);
        Assert.Contains($"{Path.GetFileName(missing)}（缺失）", skipped);
        Assert.Contains($"{Path.GetFileName(zeroByte)}（0 字节）", skipped);
    }

    [Fact]
    public void Filter_AllUnusable_ReturnsEmptyUsableList()
    {
        var (usable, skipped) = SegmentPreflight.Filter(new[]
        {
            new RecordedSegment(string.Empty, TimeSpan.Zero, null),
            new RecordedSegment(Path.Combine(_directory, "nope.mkv"), TimeSpan.Zero, null),
        });

        Assert.Empty(usable);
        Assert.Equal(2, skipped.Count);
    }

    [Fact]
    public void Filter_NullSegments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SegmentPreflight.Filter(null!));
    }
}
