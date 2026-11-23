namespace LiveMonitor.Core.Models;

/// <summary>
/// 一次 ASR 转写的完整结果；<see cref="ClipStart"/>/<see cref="ClipEnd"/> 是相对录像文件起点的绝对时间，
/// <see cref="AsrTranscriptSegment.RelativeStart"/> 则是相对 <see cref="ClipStart"/> 的偏移。
/// </summary>
/// <param name="ClipStart">本次转写片段在录像中的起点。</param>
/// <param name="ClipEnd">本次转写片段在录像中的终点。</param>
/// <param name="FullText">整段转写文本（可能为空，使用 <see cref="Segments"/> 中的文本）。</param>
/// <param name="Segments">分段结果；不支持分段时间戳的服务商回退为单条整段记录。</param>
/// <param name="ProviderLabel">产生此结果的服务商/模型标签，如 <c>groq (whisper-large-v3-turbo)</c>。</param>
/// <param name="CreatedAtUtc">结果生成（或缓存写入）时间，UTC。</param>
public sealed record AsrTranscriptResult(
    TimeSpan ClipStart,
    TimeSpan ClipEnd,
    string FullText,
    IReadOnlyList<AsrTranscriptSegment> Segments,
    string ProviderLabel,
    DateTimeOffset CreatedAtUtc);
