namespace LiveMonitor.Core.Models;

/// <summary>源视频探测结果（由 <c>ffmpeg -i</c> 的 stderr 解析得到）。</summary>
/// <param name="Duration">总时长。</param>
/// <param name="Width">视频宽度（像素）。</param>
/// <param name="Height">视频高度（像素）。</param>
/// <param name="Fps">帧率；探测不到时为 0。</param>
/// <param name="VideoCodec">编码格式，统一小写，如 <c>h264</c> / <c>hevc</c> / <c>av1</c>。</param>
public sealed record VideoMetadata(
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    string VideoCodec);
