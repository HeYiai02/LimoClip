namespace LiveMonitor.Core.Entities;

/// <summary>
/// 一套窗口方案。DockLayoutJson 保存精确的窗格树；Slots 保存每个窗格绑定哪个直播间。
/// </summary>
public sealed class LayoutProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Dock.Avalonia 序列化结果。网格行列、比例、PaneId 均在 JSON 内。</summary>
    public string? DockLayoutJson { get; set; }

    /// <summary>
    /// true 表示用户手动拖拽过分割线或停靠位置，后续增删直播间不再自动重排；
    /// false 表示按直播间数量自动适配网格。
    /// </summary>
    public bool IsCustomLayout { get; set; }

    public List<LayoutSlot> Slots { get; set; } = new();
}
