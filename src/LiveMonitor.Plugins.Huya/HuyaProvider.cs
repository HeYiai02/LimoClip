using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙平台 Provider：房间状态/开播判断走移动端 <c>mp.huya.com</c> 接口，拉流使用接口下发的带签名 FLV 地址。
/// <para>
/// 注册前调用方会自行规范化房间号；这里也容忍个性域名（如 <c>lpl</c>），由 <see cref="HuyaApiClient"/> 抓房间页解析。
/// 匿名态下虎牙只下发当前账号可播的一路流（高码率/2K 档位需要登录态 Cookie，本项目暂不自动构造），
/// 因此只暴露单档「原画」画质，保证录制与预览可用。
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
        var snapshot = await _api.GetRoomAsync(candidate, ct).ConfigureAwait(false);
        if (!snapshot.IsLive)
        {
            return RoomStatus.Offline(snapshot.RoomId, PlatformType.Huya, snapshot.RoomName, snapshot.Nick);
        }

        var quality = BuildQuality(snapshot);
        return new RoomStatus(
            snapshot.RoomId,
            PlatformType.Huya,
            snapshot.RoomName,
            snapshot.Nick,
            true,
            new[] { quality });
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

    private static StreamQuality BuildQuality(HuyaRoomSnapshot snapshot)
    {
        var line = snapshot.FlvLines
            .OrderBy(candidate => candidate.LineIndex)
            .ThenBy(candidate => candidate.WebPriorityRate)
            .First();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PlatformDefaults.UserAgent,
            ["Referer"] = $"https://www.huya.com/{snapshot.RoomId}",
        };

        // iBitRate == 0 表示当前最高档（如「蓝光10M」），用于画质下拉展示；实际码率由虎牙按登录态下发。
        var displayName = snapshot.BitRates.FirstOrDefault(bitRate => bitRate.BitRate == 0)?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = snapshot.BitRates.FirstOrDefault()?.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "原画";
        }

        return new StreamQuality(
            $"huya-flv-{line.LineIndex}",
            displayName,
            0,
            0,
            line.Url,
            headers);
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
