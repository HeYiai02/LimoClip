using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙平台 Provider：房间状态/开播判断走移动端 <c>mp.huya.com</c> 接口，拉流使用接口下发的带签名 FLV 地址。
/// <para>
/// 注册前调用方会自行规范化房间号；这里也容忍个性域名（如 <c>lpl</c>），由 <see cref="HuyaApiClient"/> 抓房间页解析。
/// 已登录时把 DPAPI 解密出的 Cookie 透传给房间接口与拉流请求：高码率档位（蓝光 4M/8M/20M）通常只有登录态才稳定下发；
/// 匿名时虎牙仍会给出可播线路，因此 Cookie 为空也能正常使用。
/// </para>
/// <para>
/// 线路选择：接口一般返回 AL/TX/HS 三条线。实测 HS（华为云）在移动网络与多数国内网络下最稳定，
/// 原有实现按 <c>lineIndex</c> 排序取的是 AL（实测 1~2 秒即被 CDN 主动断开），是「直播流频繁中断」的主因，因此这里优先 HS。
/// 画质档位：接口的 <c>bitRateInfo</c> 只是名称元数据，真实档位由拉流地址上的 <c>ratio</c> 参数决定
/// （500/2000/4000/8000/20000 对应 450P/720P/1080P），因此按档位各生成一条 quality 供窗口大小自适应选择。
/// </para>
/// </summary>
public sealed class HuyaProvider : IPlatformProvider
{
    private readonly HuyaApiClient _api;
    private readonly bool _ownsApi;

    public HuyaProvider()
        : this(new HuyaApiClient(), ownsApi: true)
    {
    }

    public HuyaProvider(HttpClient? httpClient)
        : this(new HuyaApiClient(httpClient), ownsApi: false)
    {
    }

    internal HuyaProvider(HuyaApiClient api)
        : this(api, ownsApi: false)
    {
    }

    private HuyaProvider(HuyaApiClient api, bool ownsApi)
    {
        _api = api;
        _ownsApi = ownsApi;
    }

    public PlatformType Platform => PlatformType.Huya;

    public bool CanHandle(string roomUrl) => HuyaUrlParser.CanHandle(roomUrl);

    public string ExtractRoomId(string roomUrl) => HuyaUrlParser.ExtractRoomId(roomUrl);

    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
    {
        var candidate = NormalizeRoomToken(roomId);
        var snapshot = await _api.GetRoomAsync(candidate, cookie, ct).ConfigureAwait(false);

        if (!snapshot.IsLive)
        {
            return RoomStatus.Offline(snapshot.RoomId, PlatformType.Huya, snapshot.RoomName, snapshot.Nick);
        }

        var qualities = BuildQualities(snapshot, cookie);
        return new RoomStatus(
            snapshot.RoomId,
            PlatformType.Huya,
            snapshot.RoomName,
            snapshot.Nick,
            true,
            qualities);
    }

    private static string NormalizeRoomToken(string roomId)
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
    private static IReadOnlyList<StreamQuality> BuildQualities(HuyaRoomSnapshot snapshot, string? cookie)
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

    private static HuyaStreamLine SelectStableLine(IReadOnlyList<HuyaStreamLine> lines)
        => lines
            .OrderBy(candidate => StabilityRank(candidate.CdnType))
            .ThenByDescending(candidate => candidate.WebPriorityRate)
            .ThenBy(candidate => candidate.LineIndex)
            .First();

    /// <summary>线路稳定性排序：HS 最稳，其次 AL/TX，未知 CDN 最后。</summary>
    private static int StabilityRank(string? cdnType)
        => cdnType?.Trim().ToUpperInvariant() switch
        {
            "HS" => 0,
            "AL" => 1,
            "TX" => 2,
            _ => 3,
        };

    /// <summary>把接口给的码率归一到虎牙支持的 ratio 档位；0/未知回落到 20000（蓝光20M）。</summary>
    private static int NormalizeRatio(int bitRate)
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
    private static int HeightForRatio(int ratio)
        => ratio >= 4000 ? 1080 : ratio >= 2000 ? 720 : 450;

    private static string AppendRatio(string url, int ratio)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}ratio={ratio}";
    }

    private static Dictionary<string, string> BuildHeaders(string roomId, string? cookie)
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

    /// <summary>释放本实例自建的 HTTP 资源（Provider 由插件宿主持有并随进程存活，通常无需调用）。</summary>
    internal void Dispose()
    {
        if (_ownsApi)
        {
            _api.Dispose();
        }
    }
}
