using System.Net.Http;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Plugins.Douyu;

/// <summary>斗鱼平台 Provider：betard 房间状态 + getH5Play 拉流地址。</summary>
public sealed class DouyuProvider : IPlatformProvider
{
    private readonly DouyuApiClient _api;
    private readonly bool _ownsApi;

    public DouyuProvider()
        : this(new DouyuApiClient(), true)
    {
    }

    public DouyuProvider(HttpClient? httpClient)
        : this(new DouyuApiClient(httpClient), true)
    {
    }

    internal DouyuProvider(DouyuApiClient api)
        : this(api, false)
    {
    }

    private DouyuProvider(DouyuApiClient api, bool ownsApi)
    {
        _api = api;
        _ownsApi = ownsApi;
    }

    public PlatformType Platform => PlatformType.Douyu;

    public bool CanHandle(string roomUrl) => DouyuUrlParser.CanHandle(roomUrl);

    public string ExtractRoomId(string roomUrl) => DouyuUrlParser.ExtractRoomId(roomUrl);

    public async Task<RoomStatus> GetRoomStatusAsync(string roomId, string? cookie = null, CancellationToken ct = default)
    {
        var normalized = NormalizeRoomToken(roomId);
        var snapshot = await _api.GetRoomAsync(normalized, cookie, ct);
        if (!snapshot.IsLive)
        {
            return RoomStatus.Offline(snapshot.RoomId, PlatformType.Douyu, snapshot.RoomName, snapshot.Nick);
        }

        return new RoomStatus(
            RoomId: snapshot.RoomId,
            Platform: PlatformType.Douyu,
            Title: snapshot.RoomName,
            AnchorName: snapshot.Nick,
            IsLive: true,
            AvailableQualities: snapshot.Qualities);
    }

    internal void Dispose()
    {
        if (_ownsApi)
        {
            _api.Dispose();
        }
    }

    private static string NormalizeRoomToken(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("斗鱼房间号为空。", nameof(roomId));
        }

        var trimmed = roomId.Trim();
        if (DouyuUrlParser.IsRoomNumber(trimmed))
        {
            return trimmed;
        }

        return DouyuUrlParser.TryExtractRoomId(trimmed, out var extracted) && extracted is not null
            ? extracted
            : throw new ArgumentException($"无法解析斗鱼房间号：{roomId}", nameof(roomId));
    }
}
