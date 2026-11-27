using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑台展示层的纯函数集合（文案拼装、时间/大小格式化、高能与弹幕投影）。
/// 这些逻辑原先内联在 <see cref="ClipStudioViewModel"/> 的私有方法里；
/// 搬出后不依赖 Avalonia、播放器与数据库，可直接单测，并保持界面文案逐字不变。
/// </summary>
internal static class ClipStudioPresentationLogic
{
    /// <summary>高能事件在时间轴上的投影：位置已夹到 [0, duration]。</summary>
    public readonly record struct HighlightProjection(HighlightEvent Event, TimeSpan Position);

    /// <summary>
    /// 把弹幕的原始偏移（相对录制起点）换算到合并视频时间轴；没有映射表时原样返回。
    /// </summary>
    public static IReadOnlyList<RecordedDanmakuItem> MapDanmakuOffsets(
        IReadOnlyList<RecordedDanmakuItem> items,
        SegmentOffsetMap? offsetMap)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (offsetMap is null || offsetMap.IsEmpty)
        {
            return items;
        }

        var mapped = new List<RecordedDanmakuItem>(items.Count);
        foreach (var item in items)
        {
            mapped.Add(item with { OffsetMs = offsetMap.MapOffsetMs(item.OffsetMs) });
        }

        return mapped;
    }

    /// <summary>普通录像/本地视频的副标题：房间 · 标题 · 分辨率 · fps · 编码。</summary>
    public static string BuildVideoSubtitle(RecordingSession? session, VideoMetadata metadata)
    {
        var codec = string.IsNullOrWhiteSpace(metadata.VideoCodec) ? "h264" : metadata.VideoCodec.ToUpperInvariant();
        var geometry = $"{metadata.Width}x{metadata.Height} · {metadata.Fps:0.##}fps · {codec}";
        if (session is null)
        {
            return geometry;
        }

        var title = string.IsNullOrWhiteSpace(session.StreamTitle) ? "本地录像" : session.StreamTitle;
        return $"{session.RoomDisplayName} · {title} · {geometry}";
    }

    /// <summary>边录边剪副标题：优先展示分片分辨率/fps/编码，探测不到元数据时退化为分片序号与文件大小。</summary>
    public static string BuildLiveVideoSubtitle(RecordingSession session, LiveRecordingInfo liveInfo, VideoMetadata? metadata)
    {
        if (metadata is not null && metadata.Width > 0 && metadata.Height > 0)
        {
            var codec = string.IsNullOrWhiteSpace(metadata.VideoCodec) ? "h264" : metadata.VideoCodec.ToUpperInvariant();
            return $"{session.RoomDisplayName} · 录制中 · 分片 {liveInfo.PartNumber} · {metadata.Width}x{metadata.Height} · {metadata.Fps:0.##}fps · {codec}";
        }

        return $"{session.RoomDisplayName} · 录制中 · 分片 {liveInfo.PartNumber}（{FormatFileSize(liveInfo.FileSizeBytes)}）";
    }

    /// <summary>本地文件若存在同名 .jsonl 则自动作为弹幕伴随文件。</summary>
    public static string? FindCompanionDanmaku(string videoPath)
    {
        var truncated = Path.ChangeExtension(videoPath, null);
        if (truncated is null)
        {
            return null;
        }

        var candidate = truncated + ".jsonl";
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>把时间夹到 [min, max]；与 <see cref="Math.Clamp(double,double,double)"/> 语义一致。</summary>
    public static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    /// <summary>边录边剪状态条：已录时长 + 安全可剪截止（未建立安全区时提示生成中）。</summary>
    public static string BuildLiveStatusText(TimeSpan liveRecordedDuration, TimeSpan safeClipDuration)
    {
        var recorded = RecordingUiFormat.FormatDuration(liveRecordedDuration);
        return safeClipDuration > TimeSpan.Zero
            ? $"🔴 正在录制 · 已录 {recorded}（安全可剪至 {RecordingUiFormat.FormatDuration(safeClipDuration)}）"
            : $"🔴 正在录制 · 已录 {recorded}（安全缓冲生成中）";
    }

    /// <summary>文件大小文案（0.# 精度，单位 B/KB/MB/GB/TB），与原先内联实现逐字一致。</summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    /// <summary>
    /// 把高能事件投影为时间轴标记：优先使用录制偏移毫秒，缺失时用触发时刻减会话起点；
    /// 位置夹到 [0, duration]，顺序与输入一致。
    /// </summary>
    public static IReadOnlyList<HighlightProjection> ProjectHighlights(
        IReadOnlyList<HighlightEvent> events,
        DateTimeOffset sessionStartedAt,
        TimeSpan duration,
        SegmentOffsetMap? offsetMap = null)
    {
        if (events.Count == 0)
        {
            return Array.Empty<HighlightProjection>();
        }

        var projections = new List<HighlightProjection>(events.Count);
        foreach (var highlightEvent in events)
        {
            var position = highlightEvent.RecordingOffsetMs is { } offsetMs && offsetMs > 0
                ? TimeSpan.FromMilliseconds(offsetMs)
                : highlightEvent.TriggerTime - sessionStartedAt;
            // 多分片合并后时间轴前移（重叠被裁掉）：按 sidecar 映射表换算到合并视频时间轴。
            if (offsetMap is not null)
            {
                position = offsetMap.MapOffset(position);
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }
            else if (position > duration)
            {
                position = duration;
            }

            projections.Add(new HighlightProjection(highlightEvent, position));
        }

        return projections;
    }

    /// <summary>把录制弹幕项转换为播放器/弹幕层使用的 <see cref="DanmakuMessage"/>（稳定 Id = rec-偏移毫秒）。</summary>
    public static IReadOnlyList<DanmakuMessage> ToDanmakuMessages(
        IReadOnlyList<RecordedDanmakuItem> items,
        PlatformType platform,
        string roomId,
        DateTimeOffset timestamp)
    {
        var messages = new List<DanmakuMessage>(items.Count);
        foreach (var item in items)
        {
            messages.Add(new DanmakuMessage(
                platform,
                roomId,
                $"rec-{item.OffsetMs}",
                item.SenderName,
                item.Text,
                timestamp,
                item.IsGift,
                item.IsSticker));
        }

        return messages;
    }

    /// <summary>播放器时间文本：「当前位置 / 总时长」。</summary>
    public static string BuildPositionText(TimeSpan position, TimeSpan duration) =>
        $"{TimeCodeFormat.Clock(position)} / {TimeCodeFormat.Clock(duration)}";

    /// <summary>选区时长文本（未加载视频时为 00:00）。</summary>
    public static string BuildSelectionText(TimeSpan duration, TimeSpan inPoint, TimeSpan outPoint) =>
        duration > TimeSpan.Zero ? TimeCodeFormat.Clock(outPoint - inPoint) : "00:00";

    /// <summary>弹幕信息行：无文件/无弹幕提示，或「弹幕 N 条（· 录制中）」。</summary>
    public static string BuildDanmakuInfoText(IReadOnlyCollection<RecordedDanmakuItem>? items, bool isLive)
    {
        if (items is null)
        {
            return isLive ? "弹幕录制中…" : "无伴随弹幕文件";
        }

        return $"弹幕 {items.Count} 条" + (isLive ? " · 录制中" : string.Empty);
    }

    /// <summary>边录边剪标题：「房间名 · 录制中」。</summary>
    public static string BuildLiveVideoTitle(RecordingSession session) => $"{session.RoomDisplayName} · 录制中";

    /// <summary>播放/暂停按钮字形。</summary>
    public static string PlayPauseGlyph(bool isPlaying) => isPlaying ? "⏸" : "▶";
}
