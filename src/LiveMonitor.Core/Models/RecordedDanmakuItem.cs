namespace LiveMonitor.Core.Models;

/// <summary>
/// 从录制伴随 .jsonl 反序列化出的轻量弹幕项，时间轴以录像首帧为 0 点。
/// </summary>
/// <param name="OffsetMs">相对录像首帧的毫秒偏移（&gt;= 0）。</param>
/// <param name="SenderName">发送者昵称；匿名/缺失时为空字符串。</param>
/// <param name="Text">弹幕正文；大表情保留占位文本。</param>
/// <param name="IsSticker">是否为纯大表情弹幕。</param>
/// <param name="IsGift">是否为礼物 / 醒目留言。</param>
public sealed record RecordedDanmakuItem(
    long OffsetMs,
    string SenderName,
    string Text,
    bool IsSticker,
    bool IsGift);
