namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 焦点全屏下的工作区渲染策略：焦点浮层已经完整覆盖工作区，
/// 若 Dock/自由画布继续可见，后台窗格会连同视频与弹幕叠层一起渲染，
/// 合成器每帧要多画一份全屏画面，容易让全屏弹幕掉帧。
/// 这里把「全屏时隐藏工作区、只在焦点浮层里渲染弹幕」的规则集中成纯函数，便于单测。
/// </summary>
internal static class FullscreenRenderPolicy
{
    /// <summary>Dock 宿主是否可见：焦点全屏时让位给焦点浮层。</summary>
    internal static bool ShouldShowDock(bool isWorkspaceEmpty, bool isFreeformMode, bool isFocused)
        => !isWorkspaceEmpty && !isFreeformMode && !isFocused;

    /// <summary>自由画布是否可见：焦点全屏时同样隐藏。</summary>
    internal static bool ShouldShowFreeform(bool isFreeformMode, bool isFocused)
        => isFreeformMode && !isFocused;

    /// <summary>是否还需要向工作区窗格推送弹幕：全屏时只推给焦点浮层。</summary>
    internal static bool ShouldPushDanmakuToTiles(bool isFocused) => !isFocused;
}
