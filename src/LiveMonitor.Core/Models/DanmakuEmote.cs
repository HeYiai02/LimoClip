namespace LiveMonitor.Core.Models;

/// <summary>
/// 弹幕里附带的表情元数据。
/// <para>
/// V0.2 不下载/渲染表情图片，只保留平台协议里的轻量信息，供过滤（长度折算、大表情豁免）、
/// 未来弹幕落盘与按需渲染使用。图片地址可能为空（平台未下发或解析降级）。
/// </para>
/// </summary>
/// <param name="Name">表情显示名（不含方括号），如 "doge"、"Kappa"、"赞"。</param>
/// <param name="Url">表情图片地址；平台未下发时为 null。</param>
/// <param name="Id">平台侧表情 ID（Twitch emotes tag 的 id、B 站 emoticon_unique 等），可能为 null。</param>
public sealed record DanmakuEmote(string Name, string? Url = null, string? Id = null);
