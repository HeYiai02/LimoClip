using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕渲染层的纯计算逻辑：轨道划分、弹幕运动与回收、出生时长/速度/起始位置、
/// 文本格式化与清洗、倍率与同屏上限钳制。
/// 不依赖 Avalonia 控件、共享帧循环、计时器或随机源；边界由
/// <c>DanmakuOverlayLogicTests</c> 覆盖，视图层只负责套用结果。
/// </summary>
internal static class DanmakuOverlayLogic
{
    /// <summary>弹幕字号（像素）。</summary>
    internal const double FontSize = 17;

    /// <summary>单条轨道高度（像素，最小值）。</summary>
    internal const double LaneHeight = 26;

    /// <summary>轨道区上下留白（像素）。</summary>
    internal const double LaneTopPadding = 6;

    /// <summary>同轨道相邻两条弹幕的最小间距（像素）。</summary>
    internal const double SpawnGap = 24;

    /// <summary>待渲染队列上限，超出时丢弃最旧项，不阻塞上游。</summary>
    internal const int MaxPending = 600;

    /// <summary>弹幕横穿窗格的最短/最长时长（秒）。</summary>
    internal const double MinDurationSeconds = 7;
    internal const double MaxDurationSeconds = 11;

    /// <summary>表情图片统一渲染高度（像素）。</summary>
    internal const double EmoteImageHeight = 22;

    /// <summary>出生时相对右边缘的初始偏移（像素）。</summary>
    internal const double SpawnXOffset = 4;

    /// <summary>单条弹幕最多渲染的字符数，超出部分截断并追加省略号。</summary>
    internal const int MaxTextLength = 120;

    /// <summary>同屏弹幕数钳制范围。</summary>
    internal const int MinConcurrent = 1;
    internal const int MaxConcurrentLimit = 200;

    /// <summary>弹幕移动倍率钳制范围。</summary>
    internal const double MinPlaybackRate = 0.25;
    internal const double MaxPlaybackRate = 4.0;

    /// <summary>按控件高度换算出的轨道数与该轨道实际高度。</summary>
    internal readonly record struct LaneMetrics(int LaneCount, double LaneHeight);

    /// <summary>根据控件高度换算轨道数量与轨道高度（至少 1 条轨道）。</summary>
    internal static LaneMetrics ComputeLaneMetrics(double height)
    {
        var usable = height - LaneTopPadding * 2;
        var laneCount = Math.Max(1, (int)(usable / LaneHeight));
        var laneHeight = Math.Max(LaneHeight, usable / laneCount);
        return new LaneMetrics(laneCount, laneHeight);
    }

    /// <summary>播放倍率钳制：非有限值回退 1.0，正常值夹到 0.25~4。</summary>
    internal static double ClampPlaybackRate(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinPlaybackRate, MaxPlaybackRate) : 1.0;

    /// <summary>同屏弹幕数钳制：1~200。</summary>
    internal static int ClampMaxConcurrent(int value) => Math.Clamp(value, MinConcurrent, MaxConcurrentLimit);

    /// <summary>推进一帧后的弹幕横坐标（向左移动）。</summary>
    internal static double AdvanceX(double x, double speed, double deltaSeconds) => x - speed * deltaSeconds;

    /// <summary>弹幕是否已完全移出左侧（可回收）。</summary>
    internal static bool IsOffscreen(double x, double width) => x + width < 0;

    /// <summary>按 0~1 的随机样本取横穿时长（7~11 秒）。</summary>
    internal static double DurationFor(double sample01) =>
        MinDurationSeconds + Math.Clamp(sample01, 0, 1) * (MaxDurationSeconds - MinDurationSeconds);

    /// <summary>出生速度：整条弹幕在 duration 内从右侧走完全程（含自身宽度）。</summary>
    internal static double SpeedFor(double canvasWidth, double itemWidth, double durationSeconds) =>
        durationSeconds <= 0 ? 0 : (canvasWidth + itemWidth) / durationSeconds;

    /// <summary>出生横坐标：右侧边缘再外移 4px。</summary>
    internal static double SpawnX(double canvasWidth) => canvasWidth + SpawnXOffset;

    /// <summary>
    /// 渲染文本格式化：纯大表情包显示为 <c>[表情名]</c>（V0.2 不下载图片），
    /// 行内小表情保留原始 <c>[doge]</c> 文本。V0.4 导出 .ass 时同样走这一段文本。
    /// </summary>
    internal static string FormatText(DanmakuMessage message)
    {
        if (!message.IsSticker)
        {
            return message.Content;
        }

        var name = message.PrimaryEmoteName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = message.Content;
        }

        name = name.Trim().Trim('[', ']').Trim();
        return name.Length == 0 ? message.Content : $"[{name}]";
    }

    /// <summary>清洗待渲染文本：去首尾空白、换行/制表符压成空格、超长截断加省略号。</summary>
    internal static string NormalizeText(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            trimmed = trimmed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        return trimmed.Length <= MaxTextLength
            ? trimmed
            : trimmed.Substring(0, MaxTextLength) + "…";
    }
}
