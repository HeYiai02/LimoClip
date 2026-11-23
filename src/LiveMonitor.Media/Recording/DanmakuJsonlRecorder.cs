using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 弹幕伴随文件写入器：把录制期间的弹幕按行写入 <c>.jsonl</c>。
/// <para>
/// 第一行为 meta：<c>{"type":"meta","version":1,"sessionId","platform","roomId","startedAtUtc"}</c>；
/// 之后每条为 dm：<c>{"type":"dm","offsetMs","ts","uid","uname","text","isSticker","isGift"}</c>，
/// <c>offsetMs</c> 为相对首帧锚点（<c>UpdateAnchor</c>）的毫秒数，供 V0.4 剪辑台对齐时间轴。
/// 写入在独立后台任务中进行，每 2 秒 Flush 一次；<see cref="DisposeAsync"/> 会排空队列并收尾。
/// </para>
/// </summary>
public sealed class DanmakuJsonlRecorder : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAnchorWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 让中文昵称与弹幕正文以可读的 UTF-8 写入（默认序列化器会转义成 \uXXXX），
    /// 便于 V0.4 剪辑台直接查看与检索；仍是合法 JSON。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Guid _sessionId;
    private readonly PlatformType _platform;
    private readonly string _roomId;
    private readonly TimeSpan _anchorWaitTimeout;
    private readonly Action<string>? _log;
    private readonly DateTimeOffset _fallbackAnchorUtc;
    private readonly Channel<DanmakuMessage> _channel =
        Channel.CreateUnbounded<DanmakuMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource<DateTimeOffset> _anchorCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _writerTask;
    private CancellationTokenSource? _cts;
    private long _anchorUtcTicks;
    private long _writtenCount;
    private int _disposed;

    public DanmakuJsonlRecorder(
        string filePath,
        Guid sessionId,
        PlatformType platform,
        string roomId,
        Action<string>? log = null,
        TimeSpan? anchorWaitTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        _filePath = filePath;
        _sessionId = sessionId;
        _platform = platform;
        _roomId = roomId.Trim();
        _log = log;
        _anchorWaitTimeout = anchorWaitTimeout ?? DefaultAnchorWaitTimeout;
        _fallbackAnchorUtc = DateTimeOffset.UtcNow;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>首帧锚点（UTC），尚未校准时为 null。</summary>
    public DateTimeOffset? AnchorUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _anchorUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>已写入的弹幕行数（不含 meta 行）。</summary>
    public int WrittenCount => (int)Interlocked.Read(ref _writtenCount);

    /// <summary>写入任务完成后结束；异常在任务中记录日志，不向调用方抛出。</summary>
    public Task Completion => _writerTask;

    /// <summary>校准首帧锚点；只有第一次调用生效（与 ffmpeg 首帧锚点保持一致）。</summary>
    public void UpdateAnchor(DateTimeOffset anchorUtc)
    {
        if (Interlocked.CompareExchange(ref _anchorUtcTicks, anchorUtc.UtcTicks, 0) != 0)
        {
            return;
        }

        _anchorCompletion.TrySetResult(anchorUtc);
    }

    /// <summary>把一条（已过滤的）弹幕放入写入队列；Dispose 之后返回 false。</summary>
    public bool Enqueue(DanmakuMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return _channel.Writer.TryWrite(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 从未拿到首帧锚点时用创建时间兜底，保证 meta 行与排空流程能结束。
        UpdateAnchorIfMissing(_fallbackAnchorUtc);
        _channel.Writer.TryComplete();

        try
        {
            await _writerTask.WaitAsync(DisposeWaitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"弹幕伴随文件排空超时，强制结束：{_filePath}");
            _cts?.Cancel();
            try
            {
                await _writerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"弹幕伴随文件写入任务结束异常：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"弹幕伴随文件写入任务异常：{ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                NewLine = "\n",
            };

            var anchor = await ResolveAnchorAsync(ct).ConfigureAwait(false);
            await writer.WriteLineAsync(BuildMetaJson(anchor)).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);

            var reader = _channel.Reader;
            var lastFlushTimestamp = Stopwatch.GetTimestamp();

            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var message))
                {
                    await writer.WriteLineAsync(
                        BuildDanmakuJson(message, ResolveCurrentAnchor())).ConfigureAwait(false);
                    Interlocked.Increment(ref _writtenCount);
                }

                if (Stopwatch.GetElapsedTime(lastFlushTimestamp) >= FlushInterval)
                {
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    lastFlushTimestamp = Stopwatch.GetTimestamp();
                }
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke($"弹幕伴随文件写入被取消：{_filePath}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"弹幕伴随文件写入失败（{_filePath}）：{ex.Message}");
        }
    }

    private async Task<DateTimeOffset> ResolveAnchorAsync(CancellationToken ct)
    {
        var current = AnchorUtc;
        if (current is not null)
        {
            return current.Value;
        }

        try
        {
            return await _anchorCompletion.Task
                .WaitAsync(_anchorWaitTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"等待首帧锚点超时（{_anchorWaitTimeout.TotalSeconds:0.#} 秒），使用创建时间兜底：{_filePath}");
            UpdateAnchorIfMissing(_fallbackAnchorUtc);
            return _fallbackAnchorUtc;
        }
    }

    private DateTimeOffset ResolveCurrentAnchor()
    {
        var ticks = Interlocked.Read(ref _anchorUtcTicks);
        return ticks == 0 ? _fallbackAnchorUtc : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void UpdateAnchorIfMissing(DateTimeOffset anchorUtc)
    {
        if (Interlocked.CompareExchange(ref _anchorUtcTicks, anchorUtc.UtcTicks, 0) == 0)
        {
            _anchorCompletion.TrySetResult(anchorUtc);
        }
    }

    private string BuildMetaJson(DateTimeOffset anchor) => JsonSerializer.Serialize(new
    {
        type = "meta",
        version = 1,
        sessionId = _sessionId.ToString("D"),
        platform = (int)_platform,
        roomId = _roomId,
        startedAtUtc = anchor.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
    }, JsonOptions);

    private string BuildDanmakuJson(DanmakuMessage message, DateTimeOffset anchor)
    {
        var offsetMs = Math.Max(
            0,
            (message.Timestamp.UtcTicks - anchor.UtcTicks) / TimeSpan.TicksPerMillisecond);

        return JsonSerializer.Serialize(new
        {
            type = "dm",
            offsetMs,
            ts = message.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            uid = message.SenderUid,
            uname = message.SenderName,
            text = message.Content,
            isSticker = message.IsSticker,
            isGift = message.IsGiftOrSuperChat,
        }, JsonOptions);
    }
}
