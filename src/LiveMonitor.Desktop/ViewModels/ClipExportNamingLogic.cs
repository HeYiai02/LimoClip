using System;
using System.IO;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 剪辑导出的文件名规则：按选区生成默认文件名（视频名_入点-出点，超长截断到 90 字符）、
/// 时间码格式化（≥1 小时用 <c>h.mm.ss</c>，否则 <c>mm.ss</c>）以及把用户输入的文件名
/// 中的非法字符替换为下划线（清空后回退到 <c>clip_yyyyMMdd_HHmmss</c>）。
/// 抽离为纯函数以便直接单测，<see cref="ClipExportViewModel"/> 只负责调用与绑定。
/// </summary>
internal static class ClipExportNamingLogic
{
    /// <summary>默认输出文件名最大长度（含扩展名之前的主体）。</summary>
    public const int MaxOutputNameLength = 90;

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>按「视频文件名_入点-出点」生成默认输出文件名，超长时截断。</summary>
    public static string BuildDefaultOutputName(string videoPath, TimeSpan start, TimeSpan end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var startText = FormatClock(start);
        var endText = FormatClock(end);
        var name = $"{baseName}_{startText}-{endText}";
        return name.Length <= MaxOutputNameLength ? name : name[..MaxOutputNameLength];
    }

    /// <summary>选区时间码：≥1 小时为 <c>h.mm.ss</c>，否则 <c>mm.ss</c>（实现见 <see cref="TimeCodeFormat.FileSafeClock"/>）。</summary>
    public static string FormatClock(TimeSpan time) => TimeCodeFormat.FileSafeClock(time);

    /// <summary>把文件名中的非法字符替换为下划线；去空白后为空则回退到时间戳文件名。</summary>
    public static string SanitizeFileName(string? fileName, DateTimeOffset now)
    {
        var source = (fileName ?? string.Empty).Trim();
        var builder = new System.Text.StringBuilder(source.Length);
        foreach (var ch in source)
        {
            builder.Append(Array.IndexOf(InvalidFileNameChars, ch) >= 0 ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? $"clip_{now:yyyyMMdd_HHmmss}" : sanitized;
    }
}
