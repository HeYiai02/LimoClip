using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>双版切片导出器：无弹幕版 <c>-c copy</c> 无损切割 + 有弹幕版烧录 ASS 重编码。</summary>
public interface IClipExporter
{
    /// <summary>
    /// 执行导出，返回生成的文件路径列表（顺序与导出模式一致）。
    /// </summary>
    /// <param name="request">导出请求。</param>
    /// <param name="danmakuItems">全量伴随弹幕；仅无弹幕版导出时可为 null。</param>
    /// <param name="sourceVideoCodec">源视频编码（h264/hevc），用于决定是否追加 <c>-tag:v hvc1</c>。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="ct">取消标记。</param>
    Task<IReadOnlyList<string>> ExportAsync(
        ClipExportRequest request,
        IReadOnlyList<RecordedDanmakuItem>? danmakuItems,
        string sourceVideoCodec,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken ct = default);
}
