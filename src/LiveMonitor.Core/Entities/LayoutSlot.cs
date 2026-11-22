namespace LiveMonitor.Core.Entities;

/// <summary>
/// 方案内的一个窗格绑定。同一主播可在多个方案中出现。
/// </summary>
public sealed class LayoutSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LayoutProfileId { get; set; }

    public Guid LiveRoomId { get; set; }

    /// <summary>对应 DockLayoutJson 中的窗格标识，例如 pane-1。</summary>
    public string PaneId { get; set; } = string.Empty;

    public bool DefaultMuted { get; set; } = true;

    /// <summary>该窗格默认是否显示弹幕；V0.1 只持久化与渲染开关，V0.2 接实时弹幕。</summary>
    public bool DefaultDanmakuVisible { get; set; }

    /// <summary>持久化的「主视角偏好」；运行时临时焦点不写回此字段。</summary>
    public bool IsFocusSlot { get; set; }

    public string? PreferredQualityId { get; set; }
}
