namespace LiveMonitor.Platforms;

/// <summary>
/// 版本比较（宽松语义：忽略前缀 v、忽略 prerelease 后缀、按数字段逐个比较）。
/// 同时用于「本地插件版本高于内置版本才替换」与 yt-dlp 最新版本判断。
/// </summary>
public static class PluginVersionComparer
{
    public static bool IsNewer(string? candidate, string? current)
    {
        var left = Parse(candidate);
        var right = Parse(current);
        if (left is null || right is null)
        {
            return false;
        }

        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return l > r;
            }
        }

        return false;
    }

    private static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0)
        {
            text = text[..dash];
        }

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value) || value < 0)
            {
                return null;
            }

            numbers[i] = value;
        }

        return numbers;
    }
}
