using System.Text.RegularExpressions;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Controls;

/// <summary>
/// 弹幕文本中的表情 token 拆分与表情图片 URL 解析。
/// 拆成纯文本段和 [表情名] 段，渲染层再把命中的表情段替换成静态图片。
/// </summary>
public static class DanmakuEmoteText
{
    private static readonly Regex TokenPattern = new(
        @"\[[^\[\]\r\n]{1,32}\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>拆分结果：Text 有值表示普通文本，EmoteName 有值表示 [表情名]（不含方括号）。</summary>
    public readonly record struct Part(string? Text, string? EmoteName);

    public static IReadOnlyList<Part> Split(string? content)
    {
        var parts = new List<Part>();
        if (string.IsNullOrEmpty(content))
        {
            return parts;
        }

        var index = 0;
        foreach (Match match in TokenPattern.Matches(content))
        {
            if (match.Index > index)
            {
                parts.Add(new Part(content[index..match.Index], null));
            }

            parts.Add(new Part(null, match.Value[1..^1]));
            index = match.Index + match.Length;
        }

        if (index < content.Length)
        {
            parts.Add(new Part(content[index..], null));
        }

        return parts;
    }

    /// <summary>按表情名找可用的图片地址；找不到返回 null（渲染层退回 [表情名] 文本）。</summary>
    public static string? ResolveImageUrl(IReadOnlyList<DanmakuEmote>? emotes, string? emoteName)
    {
        if (emotes is null || string.IsNullOrEmpty(emoteName))
        {
            return null;
        }

        for (var i = 0; i < emotes.Count; i++)
        {
            var emote = emotes[i];
            if (string.IsNullOrEmpty(emote.Url))
            {
                continue;
            }

            if (string.Equals(emote.Name, emoteName, StringComparison.OrdinalIgnoreCase))
            {
                return emote.Url;
            }
        }

        return null;
    }
}
