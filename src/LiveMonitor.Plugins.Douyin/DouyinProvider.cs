using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// 抖音平台 Provider：直播间链接/数字 web_rid → 拉流清晰度列表。
/// 链接只接受 live.douyin.com 形式（纯数字留给 B 站，避免路由歧义）。
/// </summary>
public sealed class DouyinProvider : IPlatformProvider
{
    private readonly DouyinApiClient _api;
    private readonly bool _ownsApi;

    public DouyinProvider() : this((HttpClient?)null)
    {
    }

    public DouyinProvider(HttpClient? httpClient)
    {
        _api = new DouyinApiClient(httpClient);
        _ownsApi = true;
    }

    internal DouyinProvider(DouyinApiClient api)
    {
        _api = api;
        _ownsApi = false;
    }

    public PlatformType Platform => PlatformType.Douyin;

    public bool CanHandle(string roomUrl) => DouyinUrlParser.CanHandle(roomUrl);

    public string ExtractRoomId(string roomUrl) => DouyinUrlParser.ExtractRoomId(roomUrl);

    public async Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? authCookie = null,
        CancellationToken ct = default)
    {
        var snapshot = await _api.GetRoomAsync(NormalizeRoomToken(roomId), authCookie, ct).ConfigureAwait(false);
        if (!snapshot.IsLive)
            return RoomStatus.Offline(snapshot.WebRid, PlatformType.Douyin, snapshot.Title, snapshot.Nick);

        return new RoomStatus(
            RoomId: snapshot.WebRid,
            Platform: PlatformType.Douyin,
            Title: snapshot.Title,
            AnchorName: snapshot.Nick,
            IsLive: true,
            AvailableQualities: snapshot.Qualities);
    }

    private static string NormalizeRoomToken(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("抖音直播间号不能为空。", nameof(roomId));

        try
        {
            return DouyinUrlParser.ExtractRoomId(roomId);
        }
        catch (ArgumentException)
        {
            return roomId.Trim();
        }
    }

    internal void Dispose()
    {
        if (_ownsApi) _api.Dispose();
    }
}
