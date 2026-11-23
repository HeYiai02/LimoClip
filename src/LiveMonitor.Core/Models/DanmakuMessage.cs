using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Models;

/// <summary>
/// 一条实时弹幕。各平台客户端解析后统一成该模型。
/// <para>
/// <see cref="Timestamp"/> 取客户端本地接收时间（UTC），不使用平台服务端时间，
/// 避免服务端时钟偏差影响高能密度检测的窗口分桶。
/// </para>
/// </summary>
public sealed record DanmakuMessage(
    PlatformType Platform,
    string RoomId,
    string SenderUid,
    string SenderName,
    string Content,
    DateTimeOffset Timestamp,
    bool IsGiftOrSuperChat = false)
{
    /// <summary>
    /// B 站未登录/游客态会把所有发送者的 uid 统一回传为 "0"，
    /// 独轮车过滤对这类消息无法按用户归组，只能跳过。
    /// </summary>
    public bool IsAnonymousSender
        => string.IsNullOrEmpty(SenderUid) || string.Equals(SenderUid, "0", StringComparison.Ordinal);
}
