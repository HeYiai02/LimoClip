using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 工作区窗格承载的播放器句柄（Desktop 层契约）。
/// 视图模型只依赖本接口，不再直接接触 <see cref="MpvPlayerView"/>；
/// 需要把控件挂到视觉树上时通过 <see cref="View"/> 取回底层控件。
/// </summary>
public interface IWorkspacePlayer : IPlayerController, IDisposable
{
    /// <summary>供 XAML 承载的底层控件；<see cref="MpvPlayerView"/> 返回自身。</summary>
    /// <remarks>Desktop 项目同时引入 WinForms，Control 必须用 Avalonia 全名消除歧义。</remarks>
    Avalonia.Controls.Control? View { get; }

    /// <summary>
    /// 房间/主播名。展示已统一收敛到窗格左上角标题栏，这里仅保留数据通道。
    /// </summary>
    string? RoomTitle { get; set; }

    /// <summary>是否处于静音状态。</summary>
    bool IsMuted { get; }

    /// <summary>当前音量，范围 0–100。</summary>
    int Volume { get; }

    /// <summary>弹幕渲染总闸（设置页全局开关）。关闭时只停渲染，弹幕连接与高能统计不受影响。</summary>
    bool DanmakuMasterEnabled { get; set; }

    /// <summary>是否渲染纯大表情包弹幕（设置页开关）；关闭时只跳过渲染，不影响高能统计与连接。</summary>
    bool DanmakuStickersEnabled { get; set; }

    /// <summary>是否把表情渲染成静态图片（设置页开关）；关闭时行内表情显示文本占位。</summary>
    bool DanmakuEmoteImagesEnabled { get; set; }

    /// <summary>把已通过过滤链的弹幕批量推给渲染层；后台线程也可调用。</summary>
    void PushDanmaku(IReadOnlyList<DanmakuMessage> messages);

    /// <summary>清空弹幕渲染层（停播、切房或关弹幕时由上层调用）。</summary>
    void ClearDanmaku();

    /// <summary>沉浸模式（桌面全屏 / 焦点模式）下由主窗口控制底部控制条的显隐。</summary>
    void SetChromeVisible(bool visible);

    /// <summary>无弹幕平台（自定义清流）隐藏窗格内的弹幕开关，避免误导；不影响其他控制条元素。</summary>
    void SetDanmakuEnabled(bool enabled);
}
