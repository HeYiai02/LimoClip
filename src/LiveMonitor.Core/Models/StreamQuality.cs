namespace LiveMonitor.Core.Models;

/// <summary>
/// 平台返回的一路可播放流。RequiredHeaders 已包含播放所需的 UA/Referer/Cookie 等。
/// <para>
/// YouTube 直播的 HLS 音视频是分离的：<see cref="StreamUrl"/> 只含视频轨，
/// 音频地址通过 <see cref="AudioStreamUrl"/> 单独给出；录音时 ffmpeg 第二路输入拉音频，
/// 播放时 mpv 通过 <c>audio-file</c> 附加音轨。
/// </para>
/// </summary>
public sealed record StreamQuality(
    string Id,
    string DisplayName,
    int Height,
    int Fps,
    string StreamUrl,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    string? AudioStreamUrl = null,
    IReadOnlyDictionary<string, string>? AudioHeaders = null)
{
    public bool HasSeparateAudio => !string.IsNullOrWhiteSpace(AudioStreamUrl);
}
