using System.Text;
using LiveMonitor.Core.Entities;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 录制产出文件的命名规则（见 ARCHITECTURE.md「9.1 录制」）：
/// 视频与弹幕伴随文件同一基础名，形如 <c>20261123_200130_#3_晚风游戏室_21452505.mkv/.jsonl</c>。
/// 断流续录会按新的开始时间生成新的一对文件，不覆盖既有文件。
/// </summary>
public static class RecordingFileNaming
{
    /// <summary>单段安全化后的最大长度，避免 Windows 路径超长。</summary>
    public const int MaxSegmentLength = 80;

    /// <summary>按直播间信息构造文件基础名（不含扩展名）。</summary>
    public static string BuildBaseName(LiveRoom room, DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(room);
        return BuildBaseName(room.DisplayNumber, room.Alias, room.RoomId, startedAtUtc);
    }

    /// <summary>按展示序号、主播名、房间号与开始时间构造文件基础名（不含扩展名）。</summary>
    public static string BuildBaseName(
        int displayNumber,
        string? alias,
        string roomId,
        DateTimeOffset startedAtUtc)
    {
        var fallback = displayNumber > 0 ? $"直播{displayNumber}" : "直播";
        var safeAlias = Sanitize(alias, fallback);
        var safeRoom = Sanitize(roomId, "room");
        var time = startedAtUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"{time}_#{displayNumber}_{safeAlias}_{safeRoom}";
    }

    /// <summary>
    /// 把任意文本清洗成 Windows 合法文件名片段：非法字符/控制字符替换为下划线，
    /// 去掉首尾空白与点，超长截断；清洗后为空时返回 <paramref name="fallback"/>。
    /// </summary>
    public static string Sanitize(string? value, string fallback = "未命名")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsControl(ch) || Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        if (sanitized.Length == 0)
        {
            return fallback;
        }

        return sanitized.Length <= MaxSegmentLength ? sanitized : sanitized[..MaxSegmentLength];
    }
}
