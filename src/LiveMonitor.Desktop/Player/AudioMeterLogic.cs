using System.Globalization;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 音量表（mpv <c>astats</c> 命名管道）的纯逻辑：filter 字符串转义、RMS 电平解析、
/// dB → 0~1 映射与表针上升/下降平滑。与 mpv、Avalonia、计时器和文件 IO 无关，
/// 便于单测；文件读取与定时器仍留在 <see cref="MpvPlayerView"/> 中。
/// </summary>
internal static class AudioMeterLogic
{
    /// <summary>ffmpeg <c>ametadata=mode=print</c> 输出的 RMS 电平键。</summary>
    internal const string RmsKey = "lavfi.astats.Overall.RMS_level=";

    /// <summary>表针静音下限（dB）。低于该值显示为 0。</summary>
    internal const double MinimumDecibels = -60d;

    /// <summary>下降平滑系数（保留旧值比例）。</summary>
    internal const double FalloffFactor = 0.72d;

    /// <summary>下降平滑系数（靠近新值比例），与 <see cref="FalloffFactor"/> 之和为 1。</summary>
    internal const double FalloffRemainder = 0.28d;

    /// <summary>只读文件尾的最大字节数，避免长时间运行时把整个日志读进内存。</summary>
    internal const int MaxTailBytes = 8192;

    /// <summary>
    /// 构造挂到 mpv <c>af</c> 上的 astats filter。ffmpeg 的 filter 参数以 ':' 分隔，
    /// Windows 盘符必须转义；实测 ffmpeg 需要两个反斜杠（<c>E\\:/...</c>），
    /// 单反斜杠会被它当成分隔符吃掉。
    /// </summary>
    internal static string BuildMeterFilter(string path)
    {
        var escaped = path.Replace('\\', '/').Replace(":", "\\\\:");
        const string filterKey = "lavfi.astats.Overall.RMS_level";
        return $"lavfi=[astats=metadata=1:reset=5,ametadata=mode=print:key={filterKey}:file={escaped}:direct=1]";
    }

    /// <summary>
    /// 从 astats 日志文本中取最后一次 <see cref="RmsKey"/> 的 dB 值。
    /// 找不到或无法解析（含 NaN/Infinity）时返回 <see langword="null"/>。
    /// </summary>
    internal static double? FindLastRmsDecibels(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var index = text.LastIndexOf(RmsKey, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + RmsKey.Length;
        var end = text.IndexOfAny(['\r', '\n'], start);
        var valueText = end < 0 ? text[start..] : text[start..end];
        if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var decibels)
            && double.IsFinite(decibels))
        {
            return decibels;
        }

        return null;
    }

    /// <summary>
    /// RMS dB → 0~1 表针目标值：<see cref="MinimumDecibels"/> 及以下为 0，0 dB 及以上为 1；
    /// 非有限值按 0 处理。
    /// </summary>
    internal static double MapDecibelsToLevel(double decibels)
    {
        if (!double.IsFinite(decibels))
        {
            return 0d;
        }

        return Math.Clamp((decibels - MinimumDecibels) / -MinimumDecibels, 0d, 1d);
    }

    /// <summary>
    /// 表针平滑：上升立即跟随（真实电平表的 attack），下降按
    /// <see cref="FalloffFactor"/> / <see cref="FalloffRemainder"/> 指数回落。
    /// </summary>
    internal static double NextLevel(double currentLevel, double targetLevel)
    {
        return targetLevel >= currentLevel
            ? targetLevel
            : currentLevel * FalloffFactor + targetLevel * FalloffRemainder;
    }
}
