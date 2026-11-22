namespace LiveMonitor.Core.Models;

/// <summary>
/// 平台返回的一路可播放流。RequiredHeaders 已包含播放所需的 UA/Referer/Cookie 等。
/// </summary>
public sealed record StreamQuality(
    string Id,
    string DisplayName,
    int Height,
    int Fps,
    string StreamUrl,
    IReadOnlyDictionary<string, string> RequiredHeaders);
