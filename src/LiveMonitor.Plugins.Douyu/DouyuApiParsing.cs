using System.Globalization;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>
/// 斗鱼接口响应的纯解析规则（无网络）：房间信息、拉流地址、画质名、请求头与 Cookie 清洗。
/// 由 <see cref="DouyuApiClient"/> 委托调用，便于直接单测。
/// </summary>
internal static class DouyuApiParsing
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>betard 房间信息解析结果。</summary>
    internal sealed record DouyuRoomInfo(string RoomId, string RoomName, string Nick, long OwnerUid, bool IsLive);

    /// <summary>解析 betard 响应里的 <c>room</c> 对象；缺少对象时抛出中文异常。</summary>
    internal static DouyuRoomInfo ParseRoom(JsonElement root, string fallbackRoomId)
    {
        if (!root.TryGetProperty("room", out var room) || room.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"斗鱼房间信息格式异常（缺少 room 对象）：{fallbackRoomId}");
        }

        var roomName = room.TryGetProperty("room_name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var nick = room.TryGetProperty("nickname", out var nickElement)
            ? nickElement.GetString() ?? string.Empty
            : string.Empty;
        var isLive = room.TryGetProperty("show_status", out var statusElement)
            && statusElement.TryGetInt32(out var status)
            && status == 1;

        return new DouyuRoomInfo(ParseRoomId(room, fallbackRoomId), roomName, nick, ParseOwnerUid(room), isLive);
    }

    /// <summary>解析主播 UID：数字或字符串均可，缺失/非法回 0。</summary>
    internal static long ParseOwnerUid(JsonElement room)
    {
        if (!room.TryGetProperty("owner_uid", out var uidElement))
        {
            return 0;
        }

        if (uidElement.ValueKind == JsonValueKind.Number && uidElement.TryGetInt64(out var uidNumber))
        {
            return uidNumber;
        }

        if (uidElement.ValueKind == JsonValueKind.String
            && long.TryParse(uidElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uidText))
        {
            return uidText;
        }

        return 0;
    }

    /// <summary>解析房间号：数字或字符串均可，缺失回落到请求参数。</summary>
    internal static string ParseRoomId(JsonElement room, string fallbackRoomId)
    {
        if (!room.TryGetProperty("room_id", out var roomIdElement))
        {
            return fallbackRoomId;
        }

        return roomIdElement.ValueKind switch
        {
            JsonValueKind.Number when roomIdElement.TryGetInt64(out var numeric) => numeric.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => roomIdElement.GetString() ?? fallbackRoomId,
            _ => fallbackRoomId,
        };
    }

    /// <summary>把 getH5Play 响应解析成一条画质；无可用地址返回 null，接口报错抛中文异常。</summary>
    internal static StreamQuality? BuildQuality(JsonElement root, string roomId, string? cookie)
    {
        var errorCode = root.TryGetProperty("error", out var errorElement) && errorElement.TryGetInt32(out var error)
            ? error
            : 0;
        if (errorCode != 0)
        {
            var message = root.TryGetProperty("msg", out var msgElement) ? msgElement.GetString() : null;
            throw new InvalidOperationException($"斗鱼获取播放地址失败（error={errorCode}）：{message}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rtmpUrl = data.TryGetProperty("rtmp_url", out var rtmpUrlElement) ? rtmpUrlElement.GetString() : null;
        var rtmpLive = data.TryGetProperty("rtmp_live", out var rtmpLiveElement) ? rtmpLiveElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(rtmpUrl) || string.IsNullOrWhiteSpace(rtmpLive))
        {
            return null;
        }

        var rate = data.TryGetProperty("rate", out var rateElement) && rateElement.TryGetInt32(out var rateValue)
            ? rateValue
            : 0;

        return new StreamQuality(
            Id: "douyu-" + rate.ToString(CultureInfo.InvariantCulture),
            DisplayName: ResolveQualityName(data, rate),
            Height: 0,
            Fps: 0,
            StreamUrl: BuildStreamUrl(rtmpUrl!, rtmpLive!),
            RequiredHeaders: BuildHeaders(roomId, cookie));
    }

    /// <summary>拼接拉流地址：直播路径是绝对地址时直接用，否则拼到 CDN 基地址下。</summary>
    internal static string BuildStreamUrl(string baseUrl, string livePath)
    {
        if (livePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            livePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return livePath;
        }

        return baseUrl.TrimEnd('/') + "/" + livePath.TrimStart('/');
    }

    /// <summary>按选中的 rate 在 multirates 里找显示名；找不到回「原画」。</summary>
    internal static string ResolveQualityName(JsonElement data, int selectedRate)
    {
        if (data.TryGetProperty("multirates", out var rates) && rates.ValueKind == JsonValueKind.Array)
        {
            foreach (var rateElement in rates.EnumerateArray())
            {
                if (rateElement.TryGetProperty("rate", out var rateValue)
                    && rateValue.TryGetInt32(out var rate)
                    && rate == selectedRate)
                {
                    var name = rateElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name!;
                    }
                }
            }
        }

        return "原画";
    }

    /// <summary>拉流请求头：固定 UA、房间页 Referer，登录时附 Cookie。</summary>
    internal static Dictionary<string, string> BuildHeaders(string roomId, string? cookie)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = "https://www.douyu.com/" + roomId,
        };
        if (cookie is not null)
        {
            headers["Cookie"] = cookie;
        }

        return headers;
    }

    /// <summary>清洗 Cookie：去掉换行，空值返回 null（防止请求头注入）。</summary>
    internal static string? NormalizeCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var builder = new StringBuilder(cookie.Trim().Length);
        foreach (var ch in cookie.Trim())
        {
            builder.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
