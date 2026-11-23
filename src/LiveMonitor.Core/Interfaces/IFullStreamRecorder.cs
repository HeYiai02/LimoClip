using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 单路原始流录制器：-c copy 直接写 mkv，不做任何重编码。一个实例只录一个会话；
/// 生命周期与重试由 RecordingManager 负责，实现只关心「起流 → 上报进度 → 优雅收尾」。
/// </summary>
public interface IFullStreamRecorder : IAsyncDisposable
{
    /// <summary>解析到首帧进度后触发，参数为锚定后的录制起点（UTC）。</summary>
    event Action<DateTimeOffset>? OnFirstFrameAnchored;

    /// <summary>进度更新：已录制时长、已写入字节数、实时码率（kbps）。</summary>
    event Action<TimeSpan, long, double>? OnProgressUpdated;

    /// <summary>ffmpeg 进程退出：exitCode 与 stderr 摘要（成功时为 null）。</summary>
    event Action<int, string?>? OnExited;

    Task StartAsync(StreamQuality sourceQuality, string outputMkvPath, CancellationToken ct = default);

    /// <summary>写 stdin "q" 让 ffmpeg 收尾；超时后强杀进程树。</summary>
    Task StopGracefullyAsync(TimeSpan timeout);
}
