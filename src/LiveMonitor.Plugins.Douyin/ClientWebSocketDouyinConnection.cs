using System.Net.WebSockets;

namespace LiveMonitor.Plugins.Douyin;

/// <summary>
/// <see cref="IDouyinWebSocketConnection"/> 的真实实现，包装 <see cref="ClientWebSocket"/>。
/// </summary>
internal sealed class ClientWebSocketDouyinConnection : IDouyinWebSocketConnection
{
    private const int ReceiveBufferBytes = 64 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly int _maxFrameBytes;

    public ClientWebSocketDouyinConnection(int maxFrameBytes)
        : this(new ClientWebSocket(), maxFrameBytes)
    {
    }

    internal ClientWebSocketDouyinConnection(ClientWebSocket socket, int maxFrameBytes)
    {
        _socket = socket;
        _maxFrameBytes = maxFrameBytes;
    }

    public WebSocketState State => _socket.State;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        foreach (var header in headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                message.Write(buffer, 0, result.Count);
                if (message.Length > _maxFrameBytes)
                {
                    throw new InvalidOperationException("抖音弹幕数据帧超过大小限制，已断开。");
                }
            }

            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    public void Abort() => _socket.Abort();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 主动断开时忽略关闭错误。
        }

        try
        {
            _socket.Dispose();
        }
        catch
        {
            // 已释放时忽略。
        }
    }
}
