using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 单个窗格的播放器控制。实现放在 Desktop 层（LibMpv.Avalonia）。
/// 所有方法必须可重复调用；切台时先 PlayAsync 再释放旧流。
/// </summary>
public interface IPlayerController
{
    PlayerState State { get; }

    /// <summary>状态变化通知；回调可能来自 mpv 事件线程，UI 需自行切回 UI 线程。</summary>
    event EventHandler? StateChanged;

    /// <summary>打开指定流。quality.RequiredHeaders 中的请求头会原样交给 mpv。</summary>
    Task PlayAsync(StreamQuality quality, CancellationToken ct = default);

    /// <summary>在保持播放位置的前提下切换画质。</summary>
    Task SwitchQualityAsync(StreamQuality quality, CancellationToken ct = default);

    /// <summary>关闭当前流并释放解码资源；实现应把阻塞调用放到后台线程。</summary>
    Task StopAsync(CancellationToken ct = default);

    void SetMute(bool muted);

    /// <summary>音量范围 0–100。</summary>
    void SetVolume(int volume);

    /// <summary>小窗/多路模式：开启后由播放器限制为 15fps。</summary>
    void SetCompactMode(bool enabled);

    /// <summary>返回最近一次采样结果；尚未播放时返回全零快照。</summary>
    PlayerHealthMetrics GetHealthMetrics();
}
