namespace LiveMonitor.Platforms;

public static class PlatformDefaults
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public const string BilibiliApiBase = "https://api.live.bilibili.com";
    public const string BilibiliLiveBase = "https://live.bilibili.com";
    public const string TwitchBase = "https://www.twitch.tv";

    /// <summary>Cookie 会被拼进 HTTP 头，禁止 CR/LF 与首尾空白。</summary>
    public static string? NormalizeCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var trimmed = cookie.Trim();
        if (trimmed.Contains('\r') || trimmed.Contains('\n'))
        {
            throw new ArgumentException("Cookie 不能包含换行符。", nameof(cookie));
        }

        return trimmed;
    }

    public static string TrimForError(string? value, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
