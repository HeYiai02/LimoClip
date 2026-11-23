using System.Threading.Channels;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using Xunit;

namespace LiveMonitor.Platforms.Tests;

/// <summary>
/// TwitchDanmakuClient 的进程内端到端测试：用 fake IRC WebSocket 连接替代真实网络，
/// 注入 clock 保证确定性；所有等待都带 5s 上限，不依赖真实网络与真实定时器。
/// </summary>
public sealed class TwitchDanmakuClientTests
{
    private const string Channel = "somechannel";

    private static readonly DateTimeOffset FixedTime =
        new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_ConnectsToTwitchIrcAndSendsAnonymousHandshake()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);

        await client.ConnectAsync().WaitAsync(WaitTimeout);

        Assert.Equal(new Uri("wss://irc-ws.chat.twitch.tv:443"), connection.ConnectedUri);
        Assert.Equal(PlatformType.Twitch, client.Platform);
        Assert.Equal(Channel, client.RoomId);

        var sent = connection.Sent;
        Assert.Equal(4, sent.Count);
        Assert.Equal("CAP REQ :twitch.tv/tags twitch.tv/commands\r\n", sent[0]);
        Assert.Equal("PASS SCHMOOOPIIE\r\n", sent[1]);
        Assert.Matches("^NICK justinfan\\d{5}\\r\\n$", sent[2]);
        Assert.Equal("JOIN #somechannel\r\n", sent[3]);
    }

    [Fact]
    public async Task ConnectAsync_WithCookie_StillUsesAnonymousLogin()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = new TwitchDanmakuClient(
            Channel,
            "auth-token=secret; other=1",
            () => connection,
            clock: () => FixedTime);

        await client.ConnectAsync().WaitAsync(WaitTimeout);

        var sent = connection.Sent;
        Assert.Equal(4, sent.Count);
        Assert.Equal("PASS SCHMOOOPIIE\r\n", sent[1]);
        Assert.Matches("^NICK justinfan\\d{5}\\r\\n$", sent[2]);
        Assert.Equal("JOIN #somechannel\r\n", sent[3]);
    }

    [Fact]
    public async Task PrivmsgFrame_RaisesMessageReceivedWithParsedFields()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var received = new TaskCompletionSource<DanmakuMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.Push(
            "@display-name=Alice\\sSmith;login=alice;user-id=42 "
            + ":alice!alice@alice.tmi.twitch.tv PRIVMSG #SomeChannel :hello world\r\n");

        var message = await received.Task.WaitAsync(WaitTimeout);
        Assert.Equal(PlatformType.Twitch, message.Platform);
        Assert.Equal("somechannel", message.RoomId);
        Assert.Equal("42", message.SenderUid);
        Assert.Equal("Alice Smith", message.SenderName);
        Assert.Equal("hello world", message.Content);
        Assert.Equal(FixedTime, message.Timestamp);
        Assert.False(message.IsGiftOrSuperChat);
    }

    [Fact]
    public async Task PrivmsgFrameWithBits_MarksMessageAsGiftOrSuperChat()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var received = new TaskCompletionSource<DanmakuMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message => received.TrySetResult(message);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.Push(
            "@bits=100;display-name=Bob;login=bob;user-id=7 "
            + ":bob!bob@bob.tmi.twitch.tv PRIVMSG #somechannel :cheer100\r\n");

        var message = await received.Task.WaitAsync(WaitTimeout);
        Assert.Equal("7", message.SenderUid);
        Assert.Equal("Bob", message.SenderName);
        Assert.Equal("cheer100", message.Content);
        Assert.True(message.IsGiftOrSuperChat);
    }

    [Fact]
    public async Task PingFrame_RepliesPong()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.Push("PING :tmi.twitch.tv\r\n");

        var pong = await connection.PongSent.Task.WaitAsync(WaitTimeout);
        Assert.Equal("PONG :tmi.twitch.tv\r\n", pong);
    }

    [Fact]
    public async Task ReconnectMessage_RaisesDisconnectedWithNullAndStopsLoop()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.Push(":tmi.twitch.tv RECONNECT\r\n");

        var error = await disconnected.Task.WaitAsync(WaitTimeout);
        Assert.Null(error);
        Assert.True(connection.Aborted);
    }

    [Fact]
    public async Task PeerClose_RaisesDisconnectedWithNull()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.CloseFromPeer();

        var error = await disconnected.Task.WaitAsync(WaitTimeout);
        Assert.Null(error);
    }

    [Fact]
    public async Task ReceiveFailure_RaisesDisconnectedWithException()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        connection.FailFromPeer(new IOException("boom"));

        var error = await disconnected.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ConnectAsync_ConnectionFailure_ThrowsAndDisposesConnection()
    {
        var connection = new FakeTwitchIrcConnection
        {
            ConnectException = new HttpRequestException("boom")
        };
        await using var client = CreateClient(connection);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ConnectAsync().WaitAsync(WaitTimeout));

        Assert.True(connection.DisposeCount >= 1);
    }

    [Fact]
    public async Task ConnectAsync_HandshakeSendFailure_ThrowsAndDisposesConnection()
    {
        var connection = new FakeTwitchIrcConnection
        {
            SendException = new IOException("send boom")
        };
        await using var client = CreateClient(connection);

        await Assert.ThrowsAsync<IOException>(
            () => client.ConnectAsync().WaitAsync(WaitTimeout));

        Assert.True(connection.DisposeCount >= 1);
    }

    [Fact]
    public async Task DisconnectAsync_SendsQuitDoesNotRaiseDisconnectedAndIsIdempotent()
    {
        var connection = new FakeTwitchIrcConnection();
        await using var client = CreateClient(connection);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.ConnectAsync().WaitAsync(WaitTimeout);
        await client.DisconnectAsync().WaitAsync(WaitTimeout);

        Assert.Contains("QUIT :bye\r\n", connection.Sent);
        Assert.True(connection.Aborted);
        Assert.False(disconnected.Task.IsCompleted);

        // 重复 Disconnect/Dispose 不抛且不触发事件。
        await client.DisconnectAsync().WaitAsync(WaitTimeout);
        await client.DisconnectAsync().WaitAsync(WaitTimeout);
        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(disconnected.Task.IsCompleted);
        Assert.True(connection.DisposeCount >= 1);
    }

    [Fact]
    public void Constructor_NormalizesChannelLoginAndRejectsEmpty()
    {
        var connection = new FakeTwitchIrcConnection();
        var client = new TwitchDanmakuClient("#SomeChannel", null, () => connection);

        Assert.Equal("somechannel", client.RoomId);
        Assert.Throws<ArgumentException>(() => new TwitchDanmakuClient("  ", null));
        Assert.Throws<ArgumentException>(() => new TwitchDanmakuClient("#", null));
    }

    private static TwitchDanmakuClient CreateClient(
        FakeTwitchIrcConnection connection,
        string? cookie = null)
        => new(
            Channel,
            cookie,
            () => connection,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            () => FixedTime);
}

/// <summary>进程内假 IRC WebSocket：记录发送文本、可推送帧、可模拟对端关闭/异常。</summary>
internal sealed class FakeTwitchIrcConnection : ITwitchIrcConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private readonly object _sync = new();
    private readonly List<string> _sent = new();
    private int _abortCount;
    private int _disposeCount;

    public Uri? ConnectedUri { get; private set; }

    /// <summary>非 null 时 <see cref="ConnectAsync"/> 抛出该异常（模拟连接失败）。</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>非 null 时 <see cref="SendAsync"/> 抛出该异常（模拟握手发送失败）。</summary>
    public Exception? SendException { get; set; }

    public bool Aborted => Volatile.Read(ref _abortCount) > 0;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public TaskCompletionSource<string> PongSent { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_sync)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        ConnectedUri = uri;
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (SendException is not null)
        {
            throw SendException;
        }

        lock (_sync)
        {
            _sent.Add(text);
        }

        if (text.StartsWith("PONG ", StringComparison.Ordinal))
        {
            PongSent.TrySetResult(text);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
        => await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);

    public void Abort()
    {
        Interlocked.Increment(ref _abortCount);
        _incoming.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>向客户端推送一个服务器帧（可含多行）。</summary>
    public void Push(string frame) => _incoming.Writer.TryWrite(frame);

    /// <summary>模拟对端正常关闭（Close 帧）。</summary>
    public void CloseFromPeer() => _incoming.Writer.TryWrite(null);

    /// <summary>模拟接收路径异常（网络错误）。</summary>
    public void FailFromPeer(Exception error) => _incoming.Writer.TryComplete(error);
}
