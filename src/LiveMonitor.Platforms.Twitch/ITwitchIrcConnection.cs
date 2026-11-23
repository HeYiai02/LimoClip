namespace LiveMonitor.Platforms;

/// <summary>
/// Twitch IRC WebSocket 的最小抽象，只为让 <see cref="TwitchDanmakuClient"/> 在单测里
/// 可以用假连接替换真实网络（握手文本、PING/PONG、对端关闭、异常都能确定性触发）。
/// </summary>
public interface ITwitchIrcConnection : IAsyncDisposable
{
    /// <summary>建立 WebSocket 连接。</summary>
    Task ConnectAsync(Uri uri, CancellationToken ct);

    /// <summary>发送一条文本消息（调用方负责拼好行结束符）。</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>
    /// 接收一条完整文本文帧。分片消息在实现内部拼接到 EndOfMessage 后返回；
    /// 对端正常关闭时返回 null，异常断开则抛异常。
    /// </summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>立即中止连接（用于主动断开，避免等待对端的关闭握手）。</summary>
    void Abort();
}
