using System.Globalization;
using System.Text;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 把录制伴随弹幕（<see cref="RecordedDanmakuItem"/>）转换成 .ass 字幕，供 ffmpeg <c>ass=</c> 滤镜烧录。
/// 采用单轨道 R2L 滚动模式：文字从右向左匀速移动，按轨道避让防止追尾重叠。
/// </summary>
public sealed class DanmakuAssConverter : IDanmakuAssConverter
{
    private const double TrackTailGapPx = 24.0;
    private const int TrackTopPaddingPx = 12;

    private readonly record struct TrackState(double FreeAtSec, double LastEndSec, double LastWidthPx);

    /// <inheritdoc />
    public async Task<int> ConvertToAssAsync(
        IReadOnlyList<RecordedDanmakuItem> allDanmaku,
        TimeSpan start,
        TimeSpan end,
        string outputAssPath,
        DanmakuAssStyle? style = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allDanmaku);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputAssPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, start);

        style ??= new DanmakuAssStyle();
        var startMs = (long)start.TotalMilliseconds;
        var endMs = (long)end.TotalMilliseconds;

        var directory = Path.GetDirectoryName(outputAssPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // ASS Alpha 通道：00=完全不透明，FF=完全透明。
        var alpha = Math.Clamp((int)Math.Round((100 - style.OpacityPercent) / 100.0 * 255), 0, 255);
        var primaryColor = $"&H{alpha:X2}FFFFFF";
        var outlineColor = $"&H{alpha:X2}000000";

        var lineHeight = style.FontSize + 8;
        var maxTopAreaPx = (int)(style.PlayResY * Math.Clamp(style.DisplayAreaRatio, 0.25, 0.95));
        var trackCount = Math.Max(3, maxTopAreaPx / lineHeight);
        var tracks = new TrackState[trackCount];
        for (var i = 0; i < trackCount; i++)
        {
            tracks[i] = new TrackState(double.NegativeInfinity, double.NegativeInfinity, 0);
        }

        var builder = new StringBuilder(32 * 1024);
        builder.AppendLine("[Script Info]");
        builder.AppendLine("Title: LimoClip Exported Danmaku");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine($"PlayResX: {style.PlayResX}");
        builder.AppendLine($"PlayResY: {style.PlayResY}");
        builder.AppendLine("WrapStyle: 2");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, " +
            "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, " +
            "Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.AppendLine(
            $"Style: R2L,{style.FontName},{style.FontSize},{primaryColor},&H000000FF,{outlineColor}," +
            "&H80000000,-1,0,0,0,100,100,0,0,1,2,0,7,0,0,0,1");
        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var written = 0;
        var durationSec = Math.Max(3.0, style.ScrollDurationSeconds);

        foreach (var danmaku in allDanmaku)
        {
            ct.ThrowIfCancellationRequested();

            if (danmaku.OffsetMs < startMs || danmaku.OffsetMs > endMs)
            {
                continue;
            }

            if (danmaku.IsSticker && !style.IncludeStickersAsText)
            {
                continue;
            }

            var cleanText = EscapeAssText(danmaku.Text);
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                continue;
            }

            var relStartSec = (danmaku.OffsetMs - startMs) / 1000.0;
            var relEndSec = relStartSec + durationSec;

            var textWidthPx = EstimateTextWidthPx(cleanText, style.FontSize);
            var totalTravelPx = style.PlayResX + textWidthPx;
            var speedPxPerSec = totalTravelPx / durationSec;

            var chosenTrack = FindBestTrack(tracks, relStartSec, relEndSec, textWidthPx, speedPxPerSec, style.PlayResX);
            if (chosenTrack < 0)
            {
                continue; // 极端弹幕海下丢弃溢出条，防止画面糊成一团。
            }

            // 轨道在当前弹幕尾部完全进入屏幕（+24px 间距）后才可容纳下一条。
            var tailClearTime = relStartSec + (textWidthPx + TrackTailGapPx) / speedPxPerSec;
            tracks[chosenTrack] = new TrackState(tailClearTime, relEndSec, textWidthPx);

            var y = TrackTopPaddingPx + chosenTrack * lineHeight;
            var startX = style.PlayResX;
            var endX = -(int)Math.Ceiling(textWidthPx);

            builder.Append("Dialogue: 0,")
                .Append(FormatAssTime(relStartSec)).Append(',')
                .Append(FormatAssTime(relEndSec)).Append(",R2L,,0,0,0,,")
                .Append($@"{{\move({startX},{y},{endX},{y})}}")
                .AppendLine(cleanText);

            written++;
        }

        await File.WriteAllTextAsync(
                outputAssPath,
                builder.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                ct)
            .ConfigureAwait(false);

        return written;
    }

    private static int FindBestTrack(
        TrackState[] tracks,
        double startSec,
        double endSec,
        double widthPx,
        double speedPxPerSec,
        int playResX)
    {
        for (var i = 0; i < tracks.Length; i++)
        {
            var track = tracks[i];

            // 条件 1：前一条弹幕尾部已完全离开右边界。
            if (startSec < track.FreeAtSec)
            {
                continue;
            }

            // 条件 2：当前弹幕速度更快时，确保在前一条离场前不会追尾撞上。
            var reachLeftEdgeTime = startSec + playResX / speedPxPerSec;
            if (reachLeftEdgeTime < track.LastEndSec)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static double EstimateTextWidthPx(string text, int fontSize)
    {
        double units = 0;
        foreach (var c in text)
        {
            units += c <= 0x7F ? 0.56 : 1.02;
        }

        return Math.Max(fontSize, units * fontSize);
    }

    private static string EscapeAssText(string raw)
    {
        return raw.Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\\", "\\\\")
            .Replace("{", "｛")
            .Replace("}", "｝")
            .Trim();
    }

    private static string FormatAssTime(double totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        var timeSpan = TimeSpan.FromSeconds(totalSeconds);
        var centiseconds = Math.Min(99, timeSpan.Milliseconds / 10);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:D2}:{2:D2}.{3:D2}",
            (int)timeSpan.TotalHours,
            timeSpan.Minutes,
            timeSpan.Seconds,
            centiseconds);
    }
}
