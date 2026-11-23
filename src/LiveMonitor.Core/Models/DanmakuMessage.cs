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
    bool IsGiftOrSuperChat = false,
    bool IsSticker = false,
    IReadOnlyList<DanmakuEmote>? Emotes = null)
{
    /// <summary>
    /// B 站未登录/游客态会把所有发送者的 uid 统一回传为 "0"，
    /// 独轮车过滤对这类消息无法按用户归组，只能跳过。
    /// </summary>
    public bool IsAnonymousSender
        => string.IsNullOrEmpty(SenderUid) || string.Equals(SenderUid, "0", StringComparison.Ordinal);

    /// <summary>
    /// 是否为“纯大表情包”弹幕（B 站 <c>DANMU_MSG</c> 的 <c>info[0][12] == 1</c>）。
    /// 这类消息的 <see cref="Content"/> 只是表情名占位文本，真实图片见 <see cref="Emotes"/>。
    /// V0.2 把它视同短情绪词豁免独轮车规则，默认渲染为 <c>[表情名]</c> 文本。
    /// </summary>
    public bool IsSticker { get; init; } = IsSticker;

    /// <summary>
    /// 本条弹幕携带的表情列表；无表情时为 null。
    /// V0.2 仅解析元数据，不做图片下载与动态表情渲染。
    /// </summary>
    public IReadOnlyList<DanmakuEmote>? Emotes { get; init; } = Emotes;

    /// <summary>是否携带至少一个表情。</summary>
    public bool HasEmotes => Emotes is { Count: > 0 };

    /// <summary>第一个表情的显示名；无表情时为 null。</summary>
    public string? PrimaryEmoteName
        => Emotes is { Count: > 0 } ? Emotes[0].Name : null;
}
