using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Plugins.Douyu;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Douyu;

/// <summary>
/// 斗鱼弹幕客户端流接缝测试：通过内部分块流/阻塞流/记录流驱动收包与心跳循环，
/// 覆盖 ReadExact 半包与粘包、非法帧长度防御、连接中断、心跳定时、发送失败与 Dispose/取消退出。
/// 全部基于内存流与可控取消，不建立真实网络连接。
/// </summary>
public sealed class DouyuDanmakuStreamTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadExactAsync_AssemblesHeaderFromPartialReads()
    {
        var expected = new byte[] { 0x04, 0x00, 0x00, 0x00 };
        await using var stream = new ChunkedReadStream(expected, maxChunk: 1);
        var buffer = new byte[expected.Length];

        var completed = await DouyuDanmakuClient.ReadExactAsync(stream, buffer, CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.True(completed);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public async Task ReadExactAsync_ConsumesStreamInOrder_AcrossConcatenatedPackets()
    {
        var first = new byte[] { 0xAA, 0x01, 0x02, 0x03 };
        var second = new byte[] { 0xBB, 0x04, 0x05, 0x06 };
        var data = first.Concat(second).ToArray();
        await using var stream = new ChunkedReadStream(data, maxChunk: 2);
        var buffer = new byte[4];

        Assert.True(await DouyuDanmakuClient.ReadExactAsync(stream, buffer, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(first, buffer);

        // 粘包场景：上一次 ReadExact 只消费自己的 4 字节，下一包从头继续。
        Assert.True(await DouyuDanmakuClient.ReadExactAsync(stream, buffer, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(second, buffer);
    }

    [Fact]
    public async Task ReadExactAsync_ReturnsFalse_WhenStreamEndsBeforeBufferIsFull()
    {
        await using var stream = new ChunkedReadStream(new byte[] { 0x01, 0x02 }, maxChunk: 1);
        var buffer = new byte[4];

        Assert.False(await DouyuDanmakuClient.ReadExactAsync(stream, buffer, CancellationToken.None).WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task ReadExactAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        await using var stream = new BlockingReadStream();
        using var cts = new CancellationTokenSource();
        var readTask = DouyuDanmakuClient.ReadExactAsync(stream, new byte[4], cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task ReceiveLoop_ParsesSplitAndConcatenatedFrames_AndRepliesToHandshake()
    {
        var payloads = new[]
        {
            "type@=loginres/userid@=0/",
            "type@=pingreq/",
            "type@=chatmsg/uid@=42/nn@=Alice/txt@=hello@Aworld/",
            "type@=dgb/gfcnt@=1/gfid@=520/uid@=7/nn@=Bob/",
        };
        // 每帧再拆成 3 字节以内的分块，强制跨多次 ReadAsync 拼接（半包）。
        var frames = payloads.SelectMany(ServerFrame).ToArray();
        await using var stream = new DuplexRecordingStream(frames, maxReadChunk: 3);
        await using var client = new DouyuDanmakuClient("9999");
        var messages = new List<DanmakuMessage>();
        var disconnects = new List<Exception?>();
        client.MessageReceived += message => messages.Add(message);
        client.Disconnected += error => disconnects.Add(error);

        await client.RunReceiveLoopAsync(stream, CancellationToken.None).WaitAsync(WaitTimeout);

        var sent = stream.SentPayloads();
        Assert.Contains("type@=joingroup/rid@=9999/gid@=-9999/", sent);
        Assert.Contains("type@=mrkl/", sent);
        Assert.Empty(disconnects);

        Assert.Equal(2, messages.Count);
        var chat = messages[0];
        Assert.Equal(PlatformType.Douyu, chat.Platform);
        Assert.Equal("9999", chat.RoomId);
        Assert.Equal("42", chat.SenderUid);
        Assert.Equal("Alice", chat.SenderName);
        Assert.Equal("hello@world", chat.Content);
        Assert.False(chat.IsGiftOrSuperChat);

        var gift = messages[1];
        Assert.Equal("7", gift.SenderUid);
        Assert.Equal("Bob", gift.SenderName);
        Assert.Equal("赠送礼物 #520", gift.Content);
        Assert.True(gift.IsGiftOrSuperChat);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1_000_001)]
    public async Task ReceiveLoop_RaisesDisconnected_WhenDeclaredLengthIsOutOfRange(int declared)
    {
        await using var stream = new DuplexRecordingStream(BitConverter.GetBytes(declared), maxReadChunk: 2);
        await using var client = new DouyuDanmakuClient("9999");
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.RunReceiveLoopAsync(stream, CancellationToken.None).WaitAsync(WaitTimeout);
        var error = await disconnected.Task.WaitAsync(WaitTimeout);

        var invalid = Assert.IsType<InvalidDataException>(error);
        Assert.Contains("长度异常", invalid.Message);
    }

    [Fact]
    public async Task ReceiveLoop_RaisesDisconnected_WhenConnectionClosesMidFrame()
    {
        // 合法长度 12，但只给 2 字节数据就 EOF。
        var data = BitConverter.GetBytes(12).Concat(new byte[] { 0x01, 0x02 }).ToArray();
        await using var stream = new DuplexRecordingStream(data, maxReadChunk: 2);
        await using var client = new DouyuDanmakuClient("9999");
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.RunReceiveLoopAsync(stream, CancellationToken.None).WaitAsync(WaitTimeout);
        var error = await disconnected.Task.WaitAsync(WaitTimeout);

        var io = Assert.IsType<IOException>(error);
        Assert.Contains("连接已断开", io.Message);
    }

    [Fact]
    public async Task ReceiveLoop_NullStream_RaisesDisconnectedWithConnectionMissing()
    {
        await using var client = new DouyuDanmakuClient("9999");
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);

        await client.RunReceiveLoopAsync(null, CancellationToken.None).WaitAsync(WaitTimeout);
        var error = await disconnected.Task.WaitAsync(WaitTimeout);

        var invalid = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("连接未建立", invalid.Message);
    }

    [Fact]
    public async Task HeartbeatLoop_SendsMrklOnSchedule_UntilCancelled()
    {
        await using var stream = new DuplexRecordingStream(Array.Empty<byte>());
        await using var client = new DouyuDanmakuClient("9999");
        using var cts = new CancellationTokenSource();
        var heartbeat = client.RunHeartbeatLoopAsync(stream, TimeSpan.FromMilliseconds(10), cts.Token);

        await WaitUntilAsync(() => stream.WriteCount >= 2, "心跳未按预期发送。");
        cts.Cancel();

        await heartbeat.WaitAsync(WaitTimeout);

        var sent = stream.SentPayloads();
        Assert.True(sent.Count >= 2);
        Assert.All(sent, payload => Assert.Equal("type@=mrkl/", payload));
    }

    [Fact]
    public async Task HeartbeatLoop_RaisesDisconnected_WhenSendFails()
    {
        await using var stream = new ThrowingWriteStream();
        await using var client = new DouyuDanmakuClient("9999");
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += error => disconnected.TrySetResult(error);
        var heartbeat = client.RunHeartbeatLoopAsync(stream, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        var error = await disconnected.Task.WaitAsync(WaitTimeout);
        await heartbeat.WaitAsync(WaitTimeout);

        var io = Assert.IsType<IOException>(error);
        Assert.Contains("模拟写入失败", io.Message);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInternalLoops_AndReleasesReadWait()
    {
        await using var readStream = new BlockingReadStream();
        await using var heartbeatStream = new DuplexRecordingStream(Array.Empty<byte>());
        await using var client = new DouyuDanmakuClient("9999");
        var lifetime = client.ConnectionLifetimeToken;
        var receive = client.RunReceiveLoopAsync(readStream, lifetime);
        var heartbeat = client.RunHeartbeatLoopAsync(heartbeatStream, TimeSpan.FromMilliseconds(10), lifetime);

        await WaitUntilAsync(() => readStream.ReadStarted, "收包循环未进入读取等待。");
        await WaitUntilAsync(() => heartbeatStream.WriteCount >= 1, "心跳循环未启动。");

        await client.DisposeAsync().AsTask().WaitAsync(WaitTimeout);

        await receive.WaitAsync(WaitTimeout);
        await heartbeat.WaitAsync(WaitTimeout);

        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(readStream.CanceledReads >= 1);
        Assert.True(heartbeatStream.WriteCount >= 1);
    }

    [Fact]
    public async Task SendAsync_NullStream_ThrowsConnectionMissing()
    {
        await using var client = new DouyuDanmakuClient("9999");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(null, "type@=mrkl/", CancellationToken.None));

        Assert.Contains("尚未建立", exception.Message);
    }

    private static byte[] ServerFrame(string payload) => DouyuStt.BuildPacket(payload, DouyuStt.ServerTextType);

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>每次 Read 最多返回 maxChunk 字节的只读流，用于模拟网络半包。</summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxChunk;
        private int _position;

        public ChunkedReadStream(byte[] data, int maxChunk)
        {
            _data = data;
            _maxChunk = Math.Max(1, maxChunk);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = _data.Length - _position;
            if (remaining <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            var take = Math.Min(Math.Min(_maxChunk, buffer.Length), remaining);
            _data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>读取永久阻塞，直到令牌取消；记录读取是否开始与取消次数。</summary>
    private sealed class BlockingReadStream : Stream
    {
        private int _readStarted;
        private int _canceledReads;

        public bool ReadStarted => Volatile.Read(ref _readStarted) == 1;

        public int CanceledReads => Volatile.Read(ref _canceledReads);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _readStarted, 1);
            try
            {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceledReads);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>可读可写流：读侧来自分块内存数据，写侧按调用逐包记录，用于断言客户端回包。</summary>
    private sealed class DuplexRecordingStream : Stream
    {
        private readonly ChunkedReadStream _readSource;
        private readonly List<byte[]> _writes = new();

        public DuplexRecordingStream(byte[] readData, int maxReadChunk = int.MaxValue)
            => _readSource = new ChunkedReadStream(readData, maxReadChunk);

        public int WriteCount
        {
            get
            {
                lock (_writes)
                {
                    return _writes.Count;
                }
            }
        }

        public IReadOnlyList<string> SentPayloads()
        {
            byte[][] writes;
            lock (_writes)
            {
                writes = _writes.ToArray();
            }

            var payloads = new List<string>(writes.Length);
            foreach (var write in writes)
            {
                if (DouyuStt.TryParsePacket(write, write.Length, out _, out var payload))
                {
                    payloads.Add(payload.TrimEnd('\0'));
                }
            }

            return payloads;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _readSource.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _readSource.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _readSource.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _readSource.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, count));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Record(ReadOnlySpan<byte> data)
        {
            var copy = data.ToArray();
            lock (_writes)
            {
                _writes.Add(copy);
            }
        }
    }

    /// <summary>写入必然失败的流，用于验证心跳发送异常会触发 Disconnected。</summary>
    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("模拟写入失败。"));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("模拟写入失败。"));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("模拟写入失败。");
    }
}
