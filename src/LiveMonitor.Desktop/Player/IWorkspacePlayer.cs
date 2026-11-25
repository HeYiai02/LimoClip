using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Desktop.Player;

/// <summary>
/// 工作区窗格承载的播放器句柄（Desktop 层契约）。
/// 视图模型只依赖本接口，不再直接接触 <see cref="MpvPlayerView"/>；
/// 需要把控件挂到视觉树上时通过 <see cref="View"/> 取回底层控件。
/// <para>
/// 可回放 seek（进度条 / seek 轮询计时器）是播放器视图内部的 chrome，
/// 仅 <see cref="MpvPlayerView"/> 自己消费，上层从未调用，因此故意不纳入本契约。
/// </para>
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

    /// <summary>当前实际播放的画质；尚未起播时为 null。</summary>
    StreamQuality? CurrentQuality { get; }

    /// <summary>播放失败/断流后由上层做退避重连；播放器自身不解析地址。</summary>
    event EventHandler? ReconnectRequested;

    /// <summary>用户点击“刷新”按钮：由上层立即重新解析地址并重连。</summary>
    event EventHandler? RefreshRequested;

    /// <summary>重新布局时先把窗格显示成加载态，避免短暂闪成“未开播”。</summary>
    void ShowLoading(string? detail = null);

    /// <summary>主播未开播或房间已被移除时展示离线态。</summary>
    void ShowOffline(string? detail = null);

    /// <summary>起播或播放过程中失败时展示失败态。</summary>
    void ShowFailed(string? detail = null);

    /// <summary>
    /// 方案切换时尝试接管另一个句柄持有的底层播放会话（MpvContext），保持画面/音频不中断。
    /// 来源句柄类型不兼容或没有可迁移会话时返回 false，并保持双方状态不变（新句柄可安全 Dispose）。
    /// 具体类型校验由实现方完成，管理器不再对句柄实现类型做判断。
    /// </summary>
    bool TryAdoptSessionFrom(IWorkspacePlayer source, string? detail = null);
}
