namespace LiveMonitor.Core.Enums;

/// <summary>
/// 单个播放窗格的生命周期状态。占位 UI 的四态与此一一对应。
/// </summary>
public enum PlayerState
{
    /// <summary>正在解析地址并初始化播放器。</summary>
    Loading,

    /// <summary>主播当前未开播，显示离线占位并启动轮询。</summary>
    Offline,

    /// <summary>流已打开，正在播放。</summary>
    Playing,

    /// <summary>
    /// 直播流在播放中意外中断（CDN 断流、网络抖动、签名地址过期等），
    /// 与「主播未开播」区分：上层会按退避策略自动重连，界面上不能显示成未开播。
    /// </summary>
    Reconnecting,

    /// <summary>拉流或解码失败，显示失败占位，可按退避策略重试。</summary>
    Failed
}
