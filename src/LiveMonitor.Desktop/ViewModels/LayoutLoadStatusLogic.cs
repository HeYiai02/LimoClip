namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 切换布局方案后状态栏文案。抽取自 <c>MainWindowViewModel.LoadProfileAsync</c>，
/// 集中四类分支（原地保留 / 结构重建 / 差集变化 / 首次载入）的固定文案，便于直接单测。
/// </summary>
internal static class LayoutLoadStatusLogic
{
    /// <summary>窗格集合一致、不重建 Dock 树（最常见、零黑屏路径）。</summary>
    public static string InPlace(string profileName, bool isFreeform) =>
        isFreeform
            ? $"已载入方案「{profileName}」：自由布局，播放器原地保留。"
            : $"已载入方案「{profileName}」：窗格结构一致，播放器原地保留。";

    /// <summary>窗格结构变化：完整重建。</summary>
    public static string Rebuilt(string profileName, int newTileCount) =>
        $"已载入方案「{profileName}」：布局结构变化，已重建 {newTileCount} 路";

    /// <summary>首次载入或无法复用时，按新增/移除房间差集描述。</summary>
    public static string DiffChanged(string profileName, int transferred, int removed, int added) =>
        $"已载入方案「{profileName}」：复用 {transferred} 路，销毁 {removed} 路，新增 {added} 路";

    public static string Plain(string profileName) => $"已载入方案「{profileName}」";

    /// <summary>非原地路径的统一入口，按「重建 → 差集变化 → 普通」优先级选择文案。</summary>
    public static string Describe(
        string profileName,
        bool rebuilt,
        bool hasChanges,
        int newTileCount,
        int transferred,
        int removed,
        int added) =>
        rebuilt
            ? Rebuilt(profileName, newTileCount)
            : hasChanges
                ? DiffChanged(profileName, transferred, removed, added)
                : Plain(profileName);
}
