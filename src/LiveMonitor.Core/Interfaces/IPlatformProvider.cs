using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 单个直播平台的房间解析与状态查询。实现放在 LiveMonitor.Platforms。
/// </summary>
public interface IPlatformProvider
{
    PlatformType Platform { get; }

    /// <summary>判断用户输入的链接或房间号是否属于本平台。</summary>
    bool CanHandle(string roomUrl);

    /// <summary>
    /// 从链接/房间号中提取平台房间标识。无法识别时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    string ExtractRoomId(string roomUrl);

    /// <summary>
    /// 查询房间是否开播及可用画质。
    /// cookie 为可选的平台登录 Cookie；V0.1 仅焦点/原画请求携带。
    /// </summary>
    Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default);
}
