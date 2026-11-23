using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 剪辑台语音转写服务（V0.5-A）：把录像中的一小段时间区间抽音并交给云端 ASR。
/// 实现必须做本地磁盘缓存：同一文件 + 同一区间 + 同一模型命中缓存时不发起网络请求。
/// </summary>
public interface IAsrProvider
{
    /// <summary>
    /// 转写录像中的 [startTime, endTime) 区间。
    /// </summary>
    /// <param name="videoPath">本地录像文件路径。</param>
    /// <param name="startTime">片段在录像中的起始时间（&gt;= 0）。</param>
    /// <param name="endTime">片段在录像中的结束时间（必须大于 startTime，且单次不能超过 15 分钟）。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>整段文本 + 可点击跳转的分段结果。</returns>
    /// <exception cref="InvalidOperationException">未配置 API、片段过长或上游返回错误时抛出可展示给用户的消息。</exception>
    Task<AsrTranscriptResult> TranscribeSegmentAsync(
        string videoPath,
        TimeSpan startTime,
        TimeSpan endTime,
        CancellationToken ct = default);
}
