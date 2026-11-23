using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.YouTube;

/// <summary>
/// YouTube InnerTube 直播聊天页/接口响应的解析。
/// <para>
/// 先请求直播页 HTML 提取 <c>INNERTUBE_API_KEY</c>、<c>clientVersion</c> 与聊天室
/// continuation，再轮询 <c>youtubei/v1/live_chat/get_live_chat</c>。
/// </para>
/// </summary>
public static class YouTubeChatParser
{
    /// <summary>InnerTube 直播聊天接口（需拼 <c>?key={apiKey}</c>）。</summary>
    public const string ChatEndpoint = "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat";

    /// <summary>直播页里缺少 clientVersion 时的兜底值。</summary>
    public const string FallbackClientVersion = "2.20250101.00.00";

    /// <summary>聊天轮询最短间隔。</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>聊天轮询最长间隔（避免超长休眠导致弹幕严重延迟）。</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMilliseconds(15000);

    /// <summary>响应里没有 timeoutMs 时的默认轮询间隔。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(3000);

    private static readonly Regex ApiKeyRegex = new(
        "\"INNERTUBE_API_KEY\"\\s*:\\s*\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClientVersionRegex = new(
        "\"INNERTUBE_CLIENT_VERSION\"\\s*:\\s*\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LiveChatContinuationRegex = new(
        "\"liveChatRenderer\".{0,20000}?\"continuation\"\\s*:\\s*\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex ContinuationTokenRegex = new(
        "\"liveChatRenderer\".{0,20000}?\"token\"\\s*:\\s*\"(?<value>[^\"]{20,})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>直播页解析出的聊天室引导信息。</summary>
    public sealed record ChatBootstrap(string ApiKey, string ClientVersion, string Continuation);

    /// <summary>一次聊天轮询的解析结果。</summary>
    public sealed record ChatPage(
        IReadOnlyList<DanmakuMessage> Messages,
        string? NextContinuation,
        TimeSpan NextInterval);

    /// <summary>从直播页 HTML 中提取 API Key、客户端版本与聊天室 continuation。</summary>
    public static ChatBootstrap? TryParseBootstrap(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var apiKeyMatch = ApiKeyRegex.Match(html);
        if (!apiKeyMatch.Success)
        {
            return null;
        }

        var continuationMatch = LiveChatContinuationRegex.Match(html);
        if (!continuationMatch.Success)
        {
            continuationMatch = ContinuationTokenRegex.Match(html);
        }

        if (!continuationMatch.Success)
        {
            return null;
        }

        var clientVersionMatch = ClientVersionRegex.Match(html);
        var clientVersion = clientVersionMatch.Success ? clientVersionMatch.Groups["value"].Value : FallbackClientVersion;

        return new ChatBootstrap(
            apiKeyMatch.Groups["value"].Value,
            clientVersion,
            continuationMatch.Groups["value"].Value);
    }

    /// <summary>解析 get_live_chat 响应，返回本页弹幕、下一页 continuation 与建议轮询间隔。</summary>
    public static ChatPage ParseChatResponse(string json, string roomId, PlatformType platform, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!TryFindLiveChatContinuation(root, out var continuationElement))
        {
            throw new InvalidOperationException("YouTube 聊天响应缺少 liveChatContinuation。");
        }

        var messages = new List<DanmakuMessage>();
        if (continuationElement.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
            {
                var message = ParseAction(action, roomId, platform, receivedAt);
                if (message is not null)
                {
                    messages.Add(message);
                }
            }
        }

        var nextContinuation = TryGetNextContinuation(continuationElement, out var timeoutMs);
        var interval = NormalizeInterval(timeoutMs);

        return new ChatPage(messages, nextContinuation, interval);
    }

    /// <summary>按 InnerTube 约定构造聊天请求体。</summary>
    public static string BuildChatRequestBody(string continuation, string clientVersion)
    {
        var body = new
        {
            context = new
            {
                client = new
                {
                    clientName = "WEB",
                    clientVersion,
                    hl = "en",
                    gl = "US",
                    user = new { lockedSafetyMode = false }
                }
            },
            continuation,
            isReplay = false
        };

        return JsonSerializer.Serialize(body);
    }

    /// <summary>把 timeoutMs 收敛到 [1000ms, 15000ms]；缺失/非法用 3000ms。</summary>
    public static TimeSpan NormalizeInterval(double timeoutMs)
    {
        if (double.IsNaN(timeoutMs) || timeoutMs <= 0)
        {
            return DefaultPollInterval;
        }

        var value = TimeSpan.FromMilliseconds(timeoutMs);
        if (value < MinPollInterval)
        {
            return MinPollInterval;
        }

        return value > MaxPollInterval ? MaxPollInterval : value;
    }

    private static bool TryFindLiveChatContinuation(JsonElement root, out JsonElement continuation)
    {
        continuation = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("continuationContents", out var contents)
            && contents.ValueKind == JsonValueKind.Object)
        {
            if (contents.TryGetProperty("liveChatContinuation", out var liveChat)
                && liveChat.ValueKind == JsonValueKind.Object)
            {
                continuation = liveChat;
                return true;
            }

            // 少数响应直接把 liveChatContinuation 字段放在 continuationContents 下。
            if (contents.TryGetProperty("actions", out _))
            {
                continuation = contents;
                return true;
            }
        }

        return false;
    }

    private static DanmakuMessage? ParseAction(
        JsonElement action,
        string roomId,
        PlatformType platform,
        DateTimeOffset receivedAt)
    {
        if (!action.TryGetProperty("addChatItemAction", out var addChatItem)
            || !addChatItem.TryGetProperty("item", out var item))
        {
            return null;
        }

        if (item.TryGetProperty("liveChatTextMessageRenderer", out var textRenderer))
        {
            return BuildMessage(textRenderer, roomId, platform, receivedAt, isSuperChat: false, isSticker: false);
        }

        if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paidRenderer))
        {
            return BuildMessage(paidRenderer, roomId, platform, receivedAt, isSuperChat: true, isSticker: false);
        }

        if (item.TryGetProperty("liveChatPaidStickerRenderer", out var stickerRenderer))
        {
            return BuildMessage(stickerRenderer, roomId, platform, receivedAt, isSuperChat: true, isSticker: true);
        }

        return null;
    }

    private static DanmakuMessage? BuildMessage(
        JsonElement renderer,
        string roomId,
        PlatformType platform,
        DateTimeOffset receivedAt,
        bool isSuperChat,
        bool isSticker)
    {
        var content = ExtractRunsText(renderer, "message");
        if (string.IsNullOrWhiteSpace(content))
        {
            // 纯贴纸 Super Chat 没有正文，用金额/贴纸文案占位。
            content = isSticker ? "超级贴纸" : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var senderName = ExtractSimpleText(renderer, "authorName");
        var senderUid = JsonRead(renderer, "authorExternalChannelId") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(senderName))
        {
            senderName = string.IsNullOrWhiteSpace(senderUid) ? "YouTube 用户" : senderUid;
        }

        return new DanmakuMessage(
            platform,
            roomId,
            senderUid,
            senderName,
            content,
            receivedAt,
            isSuperChat,
            isSticker);
    }

    private static string? ExtractRunsText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    private static string? ExtractSimpleText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("simpleText", out var simple) && simple.ValueKind == JsonValueKind.String)
        {
            return simple.GetString();
        }

        return ExtractRunsText(parent, propertyName);
    }

    private static string? TryGetNextContinuation(JsonElement continuationElement, out double timeoutMs)
    {
        timeoutMs = 0;
        if (!continuationElement.TryGetProperty("continuations", out var continuations)
            || continuations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in continuations.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dataName in new[] { "invalidationContinuationData", "timelineContinuationData", "reloadContinuationData", "liveChatReplayContinuationData" })
            {
                if (!entry.TryGetProperty(dataName, out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var token = JsonRead(data, "continuation");
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                timeoutMs = ReadTimeout(data);
                return token;
            }
        }

        return null;
    }

    private static double ReadTimeout(JsonElement data)
    {
        if (!data.TryGetProperty("timeoutMs", out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };
    }

    private static string? JsonRead(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
