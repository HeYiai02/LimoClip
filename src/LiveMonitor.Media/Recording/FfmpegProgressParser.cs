using System.Globalization;

namespace LiveMonitor.Media.Recording;

/// <summary>解析 <c>ffmpeg -progress pipe:1</c> 单行输出得到的增量信息。</summary>
public readonly record struct FfmpegProgressUpdate(
    long? TotalSizeBytes,
    long? OutTimeMicroseconds,
    double? BitrateKbps,
    bool IsEnd);

/// <summary>
/// 解析 <c>ffmpeg -progress pipe:1</c> 的 key=value 行。
/// 关键输出：<c>total_size=</c>（字节）、<c>out_time_us=</c>（微秒，首选）、<c>out_time_ms=</c>
/// （历史字段但单位同样是微秒）、<c>out_time=HH:MM:SS.ffffff</c>、<c>bitrate=1234.5kbits/s</c>、
/// <c>progress=continue|end</c>。解析失败返回 false，不抛异常。
/// </summary>
public static class FfmpegProgressParser
{
    public static bool TryParseLine(string? line, out FfmpegProgressUpdate update)
    {
        update = default;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var key = trimmed[..separator];
        var value = trimmed[(separator + 1)..].Trim();

        switch (key)
        {
            case "total_size":
                if (TryParseLong(value, out var totalSize))
                {
                    update = new FfmpegProgressUpdate(totalSize, null, null, false);
                    return true;
                }

                return false;

            case "out_time_us":
            case "out_time_ms":
                if (TryParseLong(value, out var microseconds))
                {
                    update = new FfmpegProgressUpdate(null, microseconds, null, false);
                    return true;
                }

                return false;

            case "out_time":
                if (TryParseClockDuration(value, out var duration))
                {
                    update = new FfmpegProgressUpdate(
                        null, duration.Ticks / 10, null, false);
                    return true;
                }

                return false;

            case "bitrate":
                if (TryParseBitrateKbps(value, out var bitrateKbps))
                {
                    update = new FfmpegProgressUpdate(null, null, bitrateKbps, false);
                    return true;
                }

                return false;

            case "progress":
                update = new FfmpegProgressUpdate(null, null, null,
                    string.Equals(value, "end", StringComparison.OrdinalIgnoreCase));
                return true;

            default:
                return false;
        }
    }

    /// <summary>解析整行 <c>HH:MM:SS.ffffff</c> 形式的时长（也接受前导负号）。</summary>
    public static bool TryParseClockDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TimeSpan.TryParseExact(
            value.Trim(),
            @"hh\:mm\:ss\.ffffff",
            CultureInfo.InvariantCulture,
            out duration)
            || TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out duration);
    }

    private static bool TryParseLong(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // ffmpeg 可能输出 "N/A" 或 " 12345"。
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>解析 "1234.5kbits/s"、"N/A" 等位率文本；N/A 视为无值。</summary>
    public static bool TryParseBitrateKbps(string value, out double bitrateKbps)
    {
        bitrateKbps = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digitsEnd = trimmed.IndexOfAny(new[] { 'k', 'K', 'b', 'B', ' ', '/' });
        var numericPart = digitsEnd < 0 ? trimmed : trimmed[..digitsEnd];

        return double.TryParse(
            numericPart,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out bitrateKbps)
            && bitrateKbps >= 0;
    }
}
