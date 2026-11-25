using System.Globalization;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 播放器底栏画质文案：档位名 + 实测源帧率。
/// 平台元数据里的帧率经常是兜底猜测（B 站解析不到 60 帧时一律给 30），
/// 所以只有在 mpv 报出真实 container-fps 之后才显示帧率。
/// </summary>
public static class PlayerQualityText
{
    public const string Placeholder = "—";

    public static string Format(string? displayName, double fps)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? Placeholder : displayName.Trim();
        if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0)
        {
            return name;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{name} · {fps:0}fps");
    }
}
