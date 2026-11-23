namespace LiveMonitor.Core.Models;

/// <summary>
/// 云端 ASR 返回的单句/单段转写结果，时间轴相对本次转写片段的起点（0 = <see cref="AsrTranscriptResult.ClipStart"/>）。
/// </summary>
/// <param name="RelativeStart">相对片段起点的开始偏移（&gt;= 0）。</param>
/// <param name="RelativeEnd">相对片段起点的结束偏移（&gt;= RelativeStart）。</param>
/// <param name="Text">该段识别文本（已 Trim，非空）。</param>
public sealed record AsrTranscriptSegment(
    TimeSpan RelativeStart,
    TimeSpan RelativeEnd,
    string Text);
