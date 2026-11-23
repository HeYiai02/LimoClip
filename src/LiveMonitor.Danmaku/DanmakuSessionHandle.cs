using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku;

/// <summary>
/// 一个 (平台, 房间号) 弹幕会话的引用句柄。每次 <see cref="DanmakuManager.Acquire"/> 返回独立句柄，
/// 调用 <see cref="Dispose"/> 释放一次引用；引用计数归零时管理器在后台停止连接。
/// <para>
/// <see cref="MessagesReceived"/> 在后台线程按 batchInterval 批量触发；句柄释放后不再触发。
/// 计数属性线程安全，可随时读取。
/// </para>
/// </summary>
public sealed class DanmakuSessionHandle : IDisposable
{
    private readonly DanmakuManager _manager;
    private readonly object _deliveryGate = new();
    private int _disposed;
    private long _messageCount;
    private long _blockedCount;

    internal DanmakuSessionHandle(DanmakuManager manager, RoomKey key, PlatformType platform, string roomId)
    {
        _manager = manager;
        Key = key;
        Platform = platform;
        RoomId = roomId;
    }

    internal RoomKey Key { get; }

    /// <summary>所属直播平台。</summary>
    public PlatformType Platform { get; }

    /// <summary>房间号（已去首尾空白，保持调用方传入的大小写）。</summary>
    public string RoomId { get; }

    /// <summary>该句柄累计收到的、已通过过滤的弹幕条数（按批量投递时累计）。</summary>
    public long MessageCount => Interlocked.Read(ref _messageCount);

    /// <summary>该句柄累计被独轮车过滤器拦截的弹幕条数。</summary>
    public long BlockedCount => Interlocked.Read(ref _blockedCount);

    /// <summary>批量弹幕事件（后台线程、每 batchInterval 触发一次）。</summary>
    public event Action<IReadOnlyList<DanmakuMessage>>? MessagesReceived;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>释放一次会话引用；重复调用是空操作。归零时后台断连，不阻塞调用线程。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _manager.ReleaseHandle(this);
    }

    internal void IncrementMessageCount(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _messageCount, count);
    }

    internal void IncrementBlockedCount() => Interlocked.Increment(ref _blockedCount);

    internal void RaiseMessages(IReadOnlyList<DanmakuMessage> batch)
    {
        // 与 Dispose 串行化：Dispose 之后到达的批量不会再触发（已在执行的回调可能仍在收尾）。
        lock (_deliveryGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var handler = MessagesReceived;
            if (handler is null)
            {
                return;
            }

            try
            {
                handler(batch);
            }
            catch (Exception ex)
            {
                // 订阅方异常不能打断会话的批处理循环。
                _manager.ReportHandlerError($"MessagesReceived 订阅者异常：{ex.Message}");
            }
        }
    }
}
