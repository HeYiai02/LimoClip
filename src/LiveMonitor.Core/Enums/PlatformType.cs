namespace LiveMonitor.Core.Enums;

/// <summary>
/// 支持接入的直播平台。
/// 数值显式固定，避免后续插入平台时破坏数据库中的既有值。
/// </summary>
public enum PlatformType
{
    Bilibili = 1,
    Douyin = 2,
    Douyu = 3,
    Huya = 4,
    Twitch = 5,
    YouTube = 6,

    /// <summary>
    /// 自定义清流直链（HLS / HTTP-FLV / RTMP / RTSP / SRT 等）。
    /// 不属于任何第三方平台，无弹幕、无房间号查询，由内置 <c>CustomStreamProvider</c> 直接交给 libmpv / FFmpeg 拉流。
    /// </summary>
    CustomStream = 99
}
