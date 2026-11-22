using LiveMonitor.Core.Enums;

namespace LiveMonitor.Core.Entities;

/// <summary>
/// 直播间主数据。只描述平台连接信息与展示名，不耦合任何窗口位置。
/// </summary>
public sealed class LiveRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>全局唯一展示序号，UI 显示为「#序号 主播名」。</summary>
    public int DisplayNumber { get; set; }

    public PlatformType Platform { get; set; }

    /// <summary>平台侧的原始房间号或房间标识。</summary>
    public string RoomId { get; set; } = string.Empty;

    /// <summary>用户最初输入的完整链接。</summary>
    public string RawUrl { get; set; } = string.Empty;

    /// <summary>纯主播名，不含「#序号」前缀。</summary>
    public string Alias { get; set; } = string.Empty;

    public string? GroupTag { get; set; }

    /// <summary>以 UTC 保存；UI 负责转换成本地时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
