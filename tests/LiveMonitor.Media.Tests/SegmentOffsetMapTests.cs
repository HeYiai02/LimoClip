using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

/// <summary>
/// 合并视频时间轴映射（sidecar <c>.offsets.json</c>）的纯逻辑与文件读写单测：
/// 重叠裁剪后的偏移换算、空档/越界夹取、旧数据回退、sidecar 往返与损坏容错。
/// </summary>
public sealed class SegmentOffsetMapTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests",
        "SegmentOffsetMap",
        Guid.NewGuid().ToString("N"));

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

    // ---- MapOffset ----

    [Fact]
    public void MapOffset_NoTrim_IsIdentity()
    {
        var map = SegmentOffsetMap.Create(
            new[]
            {
                Segment(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Segment(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
            },
            new[] { TimeSpan.Zero, TimeSpan.Zero });

        Assert.Equal(TimeSpan.FromSeconds(5), map.MapOffset(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(15), map.MapOffset(TimeSpan.FromSeconds(15)));
        Assert.Equal(20d, map.TotalContentSeconds, 6);
    }

    [Fact]
    public void MapOffset_OverlapTrim_ShiftsLaterContentBack()
    {
        // 第一段 0~10s，第二段墙钟从 8s 开始（重连重叠 2s）→ 第二段裁掉 2s，合并时间轴提前 2s。
        var map = SegmentOffsetMap.Create(
            new[]
            {
                Segment(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Segment(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)),
            },
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) });

        Assert.Equal(18d, map.TotalContentSeconds, 6);
        // 重叠区同时存在于两段：映射到第一段（合并视频保留的是前一段画面）。
        Assert.Equal(TimeSpan.FromSeconds(8.5), map.MapOffset(TimeSpan.FromSeconds(8.5)));
        Assert.Equal(TimeSpan.FromSeconds(10), map.MapOffset(TimeSpan.FromSeconds(10)));
        // 第二段中段：10 + (15 - 8 - 2) = 15。
        Assert.Equal(TimeSpan.FromSeconds(15), map.MapOffset(TimeSpan.FromSeconds(15)));
        // 末段之后夹到总内容时长。
        Assert.Equal(TimeSpan.FromSeconds(18), map.MapOffset(TimeSpan.FromSeconds(19)));
    }

    [Fact]
    public void MapOffset_Gap_SnapsToNextSegmentStart()
    {
        // 第一段 0~10s，第二段墙钟 15s 才开始（中间断流 5s 没有画面）。
        var map = SegmentOffsetMap.Create(
            new[]
            {
                Segment(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Segment(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10)),
            },
            new[] { TimeSpan.Zero, TimeSpan.Zero });

        Assert.Equal(TimeSpan.FromSeconds(10), map.MapOffset(TimeSpan.FromSeconds(12)));
        Assert.Equal(TimeSpan.FromSeconds(11), map.MapOffset(TimeSpan.FromSeconds(16)));
    }

    [Fact]
    public void MapOffset_BeforeFirstSegment_ClampsToStart()
    {
        var map = SegmentOffsetMap.Create(
            new[] { Segment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)) },
            new[] { TimeSpan.Zero });

        Assert.Equal(TimeSpan.Zero, map.MapOffset(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MapOffset_Empty_ReturnsOriginal()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(7),
            SegmentOffsetMap.Empty.MapOffset(TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void MapOffsetMs_RoundsAndClampsNegative()
    {
        var map = SegmentOffsetMap.Create(
            new[]
            {
                Segment(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Segment(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10)),
            },
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) });

        // 第二段从 9s 起、裁掉开头 1s → 原 10.5s 落在 10 + (10.5 - 9 - 1) = 10.5s。
        Assert.Equal(10_500L, map.MapOffsetMs(10_500));
        Assert.Equal(0L, map.MapOffsetMs(-500));
    }

    [Fact]
    public void Create_FallsBackToCumulativeStartWhenOffsetIsMissing()
    {
        var map = SegmentOffsetMap.Create(
            new[]
            {
                new RecordedSegment("a.mkv", TimeSpan.FromSeconds(10), null),
                new RecordedSegment("b.mkv", TimeSpan.FromSeconds(10), null),
            },
            new[] { TimeSpan.Zero, TimeSpan.Zero });

        Assert.Equal(0d, map.Segments[0].OriginalStartSeconds, 6);
        Assert.Equal(10d, map.Segments[1].OriginalStartSeconds, 6);
        Assert.Equal(TimeSpan.FromSeconds(15), map.MapOffset(TimeSpan.FromSeconds(15)));
    }

    // ---- sidecar 文件 ----

    [Fact]
    public void WriteAndLoad_RoundTrips()
    {
        var videoPath = Path.Combine(_directory, "merged.mkv");
        var map = SegmentOffsetMap.Create(
            new[]
            {
                Segment(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Segment(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)),
            },
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) });

        SegmentOffsetMapFile.Write(videoPath, map);

        Assert.Equal(Path.Combine(_directory, "merged.offsets.json"), SegmentOffsetMapFile.GetPath(videoPath));
        Assert.True(File.Exists(SegmentOffsetMapFile.GetPath(videoPath)));

        var loaded = SegmentOffsetMapFile.TryLoad(videoPath);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Segments.Count);
        Assert.Equal(18d, loaded.TotalContentSeconds, 6);
        Assert.Equal(TimeSpan.FromSeconds(15), loaded.MapOffset(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void TryLoad_MissingOrCorrupt_ReturnsNull()
    {
        var videoPath = Path.Combine(_directory, "missing.mkv");
        Assert.Null(SegmentOffsetMapFile.TryLoad(videoPath));

        Directory.CreateDirectory(_directory);
        File.WriteAllText(SegmentOffsetMapFile.GetPath(videoPath), "not-json");
        Assert.Null(SegmentOffsetMapFile.TryLoad(videoPath));
    }

    [Fact]
    public void TryDelete_RemovesSidecarSilently()
    {
        var videoPath = Path.Combine(_directory, "merged.mkv");
        SegmentOffsetMapFile.Write(
            videoPath,
            SegmentOffsetMap.Create(new[] { Segment(TimeSpan.Zero, TimeSpan.FromSeconds(5)) }, new[] { TimeSpan.Zero }));

        SegmentOffsetMapFile.TryDelete(videoPath);
        Assert.False(File.Exists(SegmentOffsetMapFile.GetPath(videoPath)));

        // 不存在时幂等。
        SegmentOffsetMapFile.TryDelete(videoPath);
    }

    private static RecordedSegment Segment(TimeSpan startOffset, TimeSpan duration)
        => new($"{Guid.NewGuid():N}.mkv", duration, null, startOffset);
}
