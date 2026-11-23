using System.Net.WebSockets;

namespace LiveMonitor.Platforms;

/// <summary>
/// <see cref="IBilibiliWebSocketConnection"/> 的真实实现，包装 <see cref="ClientWebSocket"/>。
/// </summary>
public sealed class ClientWebSocketBilibiliConnection : IBilibiliWebSocketConnection
{
    private readonly ClientWebSocket _socket;

    public ClientWebSocketBilibiliConnection()
        : this(new ClientWebSocket())
    {
    }

    internal ClientWebSocketBilibiliConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
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
            }

            if (result.EndOfMessage)
            {
                return message.Length == 0 ? Array.Empty<byte>() : message.ToArray();
            }
        }
    }

    public void Abort() => _socket.Abort();

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
