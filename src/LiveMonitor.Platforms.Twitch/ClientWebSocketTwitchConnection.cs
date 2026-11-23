using System.Net.WebSockets;
using System.Text;

namespace LiveMonitor.Platforms;

/// <summary>
/// <see cref="ITwitchIrcConnection"/> 的真实实现，包装 <see cref="ClientWebSocket"/>。
/// 只处理文本帧；分片文本按字节拼接后统一 UTF-8 解码，避免多字节字符被分片截断。
/// </summary>
public sealed class ClientWebSocketTwitchConnection : ITwitchIrcConnection
{
    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
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
                return message.Length == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
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
