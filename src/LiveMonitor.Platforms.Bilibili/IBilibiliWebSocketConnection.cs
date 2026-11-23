namespace LiveMonitor.Platforms;

/// <summary>
/// B 站弹幕 WebSocket 的最小抽象，只为让 <see cref="BilibiliDanmakuClient"/> 在单测里
/// 可以用假连接替换真实网络（认证包、心跳包、收包、对端关闭都能确定性触发）。
/// </summary>
public interface IBilibiliWebSocketConnection : IAsyncDisposable
{
    /// <summary>建立 WebSocket 连接。</summary>
    Task ConnectAsync(Uri uri, CancellationToken ct);

    /// <summary>发送一条二进制消息。</summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>
    /// 接收一条完整消息。分片消息在实现内部拼接到 EndOfMessage 后返回；
    /// 对端正常关闭时返回 null，异常断开则抛异常。
    /// </summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);

    /// <summary>立即中止连接（用于主动断开，避免等待对端的关闭握手）。</summary>
    void Abort();
}
