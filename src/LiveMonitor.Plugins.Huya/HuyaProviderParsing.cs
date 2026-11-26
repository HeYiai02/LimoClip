using LiveMonitor.Core.Models;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙拉流侧的纯规则（无网络）：房间号归一、FLV 线路稳定性选择、码率→ratio 档位归一、
/// 各档画质生成与请求头构造。由 <see cref="HuyaProvider"/> 委托调用，便于直接单测。
/// </summary>
internal static class HuyaProviderParsing
{
    /// <summary>房间号归一：数字原样返回，链接/个性域名交给 <see cref="HuyaUrlParser"/>，兜底用去空白原文。</summary>
    internal static string NormalizeRoomToken(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("虎牙房间号不能为空。", nameof(roomId));
        }

        try
        {
            return HuyaUrlParser.ExtractRoomId(roomId);
        }
        catch (ArgumentException)
        {
            return roomId.Trim();
        }
    }

    /// <summary>按接口下发的档位生成多条画质；线路固定选最稳定的一条。</summary>
    internal static IReadOnlyList<StreamQuality> BuildQualities(HuyaRoomSnapshot snapshot, string? cookie)
    {
        if (snapshot.FlvLines.Count == 0)
        {
            return Array.Empty<StreamQuality>();
        }

        var line = SelectStableLine(snapshot.FlvLines);
        var headers = BuildHeaders(snapshot.RoomId, cookie);
        var maxRatio = NormalizeRatio(snapshot.MaxBitRate);

        var entries = new List<(string DisplayName, int Ratio)>();
        foreach (var bitRate in snapshot.BitRates)
        {
            // iBitRate == 0 表示当前最高档（如「蓝光20M」），真实档位取房间接口的 bitRate 字段。
            var ratio = bitRate.BitRate > 0 ? NormalizeRatio(bitRate.BitRate) : maxRatio;
            entries.Add((bitRate.DisplayName, ratio));
        }

        if (entries.Count == 0)
        {
            entries.Add(("原画", maxRatio));
        }

        var seen = new HashSet<int>();
        var qualities = new List<StreamQuality>(entries.Count);
        foreach (var (displayName, ratio) in entries.OrderByDescending(entry => entry.Ratio))
        {
            if (!seen.Add(ratio))
            {
                continue;
            }

            qualities.Add(new StreamQuality(
                $"huya-flv-{ratio}",
                string.IsNullOrWhiteSpace(displayName) ? "原画" : displayName,
                HeightForRatio(ratio),
                Fps: 30,
                AppendRatio(line.Url, ratio),
                headers));
        }

        return qualities;
    }

    /// <summary>线路稳定性：HS 最稳，其次 AL/TX，未知 CDN 最后；同档按 webPriorityRate 高→低、lineIndex 小→大。</summary>
    internal static HuyaStreamLine SelectStableLine(IReadOnlyList<HuyaStreamLine> lines)
        => lines
            .OrderBy(candidate => StabilityRank(candidate.CdnType))
            .ThenByDescending(candidate => candidate.WebPriorityRate)
            .ThenBy(candidate => candidate.LineIndex)
            .First();

    internal static int StabilityRank(string? cdnType)
        => cdnType?.Trim().ToUpperInvariant() switch
        {
            "HS" => 0,
            "AL" => 1,
            "TX" => 2,
            _ => 3,
        };

    /// <summary>把接口给的码率归一到虎牙支持的 ratio 档位；0/未知回落到 20000（蓝光20M）。</summary>
    internal static int NormalizeRatio(int bitRate)
        => bitRate switch
        {
            >= 20000 => 20000,
            >= 8000 => 8000,
            >= 4000 => 4000,
            >= 2000 => 2000,
            500 => 500,
            _ => 20000,
        };

    /// <summary>ratio → 分辨率高度，供 QualityPolicy 按窗格大小选档。</summary>
    internal static int HeightForRatio(int ratio)
        => ratio >= 4000 ? 1080 : ratio >= 2000 ? 720 : 450;

    internal static string AppendRatio(string url, int ratio)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}ratio={ratio}";
    }

    internal static Dictionary<string, string> BuildHeaders(string roomId, string? cookie)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PlatformDefaults.UserAgent,
            ["Referer"] = $"https://www.huya.com/{roomId}",
        };

        var safeCookie = HuyaApiClient.NormalizeCookie(cookie);
        if (safeCookie is not null)
        {
            headers["Cookie"] = safeCookie;
        }

        return headers;
    }
}
