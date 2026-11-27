using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 合并后视频中某一段分片的「原始时间轴 → 合并时间轴」对照。
/// 原始时间轴指相对录制起点的墙钟偏移（弹幕 <c>offsetMs</c>、高能 <c>RecordingOffsetMs</c> 用的时间；
/// 通常与 <see cref="RecordedSegment.StartOffset"/> 同一基准）。
/// </summary>
/// <param name="OriginalStartSeconds">本段在原始时间轴上的起点（秒）。</param>
/// <param name="OriginalDurationSeconds">本段实际内容时长（秒）。</param>
/// <param name="TrimmedSeconds">合并时从本段开头裁掉的重叠时长（秒）。</param>
/// <param name="MergedStartSeconds">本段内容在合并视频中的起点（秒）。</param>
public sealed record SegmentOffsetEntry(
    double OriginalStartSeconds,
    double OriginalDurationSeconds,
    double TrimmedSeconds,
    double MergedStartSeconds)
{
    /// <summary>本段在合并视频里实际保留的内容时长（已扣除裁掉的重叠开头）。</summary>
    [JsonIgnore]
    public double ContentSeconds => Math.Max(0d, OriginalDurationSeconds - TrimmedSeconds);
}

/// <summary>
/// 多分片合并后的时间轴映射表：把「相对录制起点的原始偏移」（弹幕 <c>offsetMs</c>、高能 <c>RecordingOffsetMs</c>）
/// 换算到合并视频的时间轴（重叠段被裁掉开头后，合并时间轴整体前移）。
/// <para>
/// 合并成功时由 <see cref="FfmpegSegmentMerger"/> 落盘为 <c>&lt;视频名&gt;.offsets.json</c>；
/// 剪辑台读取合并视频时按该表换算，原始 <c>.jsonl</c> 与高能数据保持不变（不复制、不改写）。
/// </para>
/// </summary>
/// <param name="Version">映射表格式版本，只在格式不兼容时升级。</param>
/// <param name="Segments">按录制顺序排列的分片对照。</param>
/// <param name="TotalContentSeconds">合并视频总内容时长（秒）。</param>
public sealed record SegmentOffsetMap(
    int Version,
    IReadOnlyList<SegmentOffsetEntry> Segments,
    double TotalContentSeconds)
{
    /// <summary>当前写入的映射表版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>空映射（单文件/未裁剪时不产生 sidecar）。</summary>
    public static SegmentOffsetMap Empty { get; } = new(CurrentVersion, Array.Empty<SegmentOffsetEntry>(), 0d);

    /// <summary>没有任何分片对照时视为无映射。</summary>
    [JsonIgnore]
    public bool IsEmpty => Segments.Count == 0;

    /// <summary>
    /// 按合并顺序生成映射表。<paramref name="trims"/> 必须与 <paramref name="segments"/> 一一对应
    /// （来自 <see cref="SegmentOverlapPlanner.ComputeStartTrims"/> 的裁剪结果）。
    /// 分片缺少墙钟起点（旧数据）时回退到「按内容时长累加」的连续时间轴。
    /// </summary>
    public static SegmentOffsetMap Create(
        IReadOnlyList<RecordedSegment> segments,
        IReadOnlyList<TimeSpan> trims)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(trims);

        var entries = new List<SegmentOffsetEntry>(segments.Count);
        var cumulativeOriginalStart = 0d;
        var mergedStart = 0d;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var duration = Math.Max(0d, segment.Duration.TotalSeconds);
            var originalStart = segment.StartOffset?.TotalSeconds ?? cumulativeOriginalStart;
            var trim = index < trims.Count ? Math.Max(0d, trims[index].TotalSeconds) : 0d;
            var content = Math.Max(0d, duration - trim);
            entries.Add(new SegmentOffsetEntry(originalStart, duration, trim, mergedStart));
            cumulativeOriginalStart += duration;
            mergedStart += content;
        }

        return new SegmentOffsetMap(CurrentVersion, entries, mergedStart);
    }

    /// <summary>
    /// 把原始偏移换算到合并后的时间轴。空表原样返回。
    /// <para>
    /// 分片在原始时间轴上可能重叠（CDN 重发），同一时刻在合并视频里只保留前一段的画面，
    /// 因此优先匹配最早包含该偏移的分片；落在两段之间的空档时贴到下一段的合并起点；
    /// 超出末段时夹到总内容时长。
    /// </para>
    /// </summary>
    public TimeSpan MapOffset(TimeSpan originalOffset)
    {
        if (Segments.Count == 0)
        {
            return originalOffset;
        }

        var seconds = Math.Max(0d, originalOffset.TotalSeconds);
        const double epsilon = 1e-6d;
        for (var index = 0; index < Segments.Count; index++)
        {
            var entry = Segments[index];
            if (seconds >= entry.OriginalStartSeconds - epsilon &&
                seconds < entry.OriginalStartSeconds + entry.OriginalDurationSeconds + epsilon)
            {
                var local = Math.Clamp(
                    seconds - entry.OriginalStartSeconds - entry.TrimmedSeconds,
                    0d,
                    entry.ContentSeconds);
                return TimeSpan.FromSeconds(entry.MergedStartSeconds + local);
            }
        }

        if (seconds < Segments[0].OriginalStartSeconds)
        {
            return TimeSpan.FromSeconds(Segments[0].MergedStartSeconds);
        }

        foreach (var entry in Segments)
        {
            if (entry.OriginalStartSeconds > seconds)
            {
                return TimeSpan.FromSeconds(entry.MergedStartSeconds);
            }
        }

        return TimeSpan.FromSeconds(TotalContentSeconds);
    }

    /// <summary>毫秒偏移版本；四舍五入到毫秒并保证非负。</summary>
    public long MapOffsetMs(long originalOffsetMs)
        => (long)Math.Round(
            MapOffset(TimeSpan.FromMilliseconds(Math.Max(0L, originalOffsetMs))).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
}

/// <summary>
/// <see cref="SegmentOffsetMap"/> 的 sidecar 文件读写：与合并视频同目录、同名，扩展名为
/// <c>.offsets.json</c>（例如 <c>xxx.mkv</c> → <c>xxx.offsets.json</c>）。
/// 读取失败（缺失/损坏/版本不符）一律返回 null，保证历史录像不受影响。
/// </summary>
public static class SegmentOffsetMapFile
{
    /// <summary>sidecar 扩展名（替换视频扩展名后使用）。</summary>
    public const string Extension = ".offsets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>返回视频对应的 sidecar 路径。</summary>
    public static string GetPath(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        return Path.ChangeExtension(videoPath, Extension);
    }

    /// <summary>写出映射表（先写临时文件再替换，避免半截 JSON）。失败时抛出 IO 异常由调用方记录。</summary>
    public static void Write(string videoPath, SegmentOffsetMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var path = GetPath(videoPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(map, JsonOptions);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>读取视频对应的映射表；不存在、损坏或版本不符时返回 null。</summary>
    public static SegmentOffsetMap? TryLoad(string videoPath)
    {
        try
        {
            var path = GetPath(videoPath);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var map = JsonSerializer.Deserialize<SegmentOffsetMap>(json, JsonOptions);
            return map is { Version: SegmentOffsetMap.CurrentVersion, Segments.Count: > 0 } ? map : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>删除过期的 sidecar（例如单分片直接移动成最终文件时不再需要映射）。</summary>
    public static void TryDelete(string videoPath)
    {
        try
        {
            var path = GetPath(videoPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
