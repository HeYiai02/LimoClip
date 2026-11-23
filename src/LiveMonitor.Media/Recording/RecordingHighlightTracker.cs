using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// 把高能事件绑定到当前正在进行中的录制会话：以 (平台, 房间号) 查活动绑定，
/// 补全 <see cref="HighlightEvent.RecordingSessionId"/> 与相对首帧的
/// <see cref="HighlightEvent.RecordingOffsetMs"/>，并累计该会话的高能点数。
/// <para>
/// 实现应保持无阻塞、不抛异常（由 DanmakuManager 在消息管线线程同步调用）。
/// </para>
/// </summary>
public sealed class RecordingHighlightTracker : IHighlightEventEnricher
{
    private readonly object _gate = new();
    private readonly Dictionary<(PlatformType Platform, string RoomId), Binding> _bindings = new();

    /// <summary>当前登记的活动绑定数量（一般等于正在录制的房间数）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bindings.Count;
            }
        }
    }

    /// <summary>登记一个正在录制的会话；重复登记同一房间会覆盖旧绑定。</summary>
    public void Register(PlatformType platform, string roomId, Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("录制会话 Id 不能为空。", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("房间号不能为空。", nameof(roomId));
        }

        lock (_gate)
        {
            _bindings[Normalize(platform, roomId)] = new Binding(sessionId);
        }
    }

    /// <summary>会话解析到首帧锚点后更新锚点，后续高能事件即可计算相对偏移。</summary>
    public void UpdateAnchor(Guid sessionId, DateTimeOffset anchorUtc)
    {
        lock (_gate)
        {
            foreach (var binding in _bindings.Values)
            {
                if (binding.SessionId == sessionId)
                {
                    binding.AnchorUtc ??= anchorUtc;
                }
            }
        }
    }

    /// <summary>移除房间绑定并返回该会话累计的高能点数。</summary>
    public int Remove(PlatformType platform, string roomId)
    {
        lock (_gate)
        {
            var key = Normalize(platform, roomId);
            if (!_bindings.TryGetValue(key, out var binding))
            {
                return 0;
            }

            _bindings.Remove(key);
            return binding.HighlightCount;
        }
    }

    /// <summary>读取某会话累计的高能点数（绑定已移除后返回 0）。</summary>
    public int GetHighlightCount(Guid sessionId)
    {
        lock (_gate)
        {
            foreach (var binding in _bindings.Values)
            {
                if (binding.SessionId == sessionId)
                {
                    return binding.HighlightCount;
                }
            }

            return 0;
        }
    }

    /// <inheritdoc />
    public HighlightEvent Enrich(HighlightEvent highlightEvent)
    {
        ArgumentNullException.ThrowIfNull(highlightEvent);

        Guid sessionId;
        long? offsetMs = null;
        lock (_gate)
        {
            if (!_bindings.TryGetValue(
                    Normalize(highlightEvent.Platform, highlightEvent.RoomId),
                    out var binding))
            {
                return highlightEvent;
            }

            sessionId = binding.SessionId;
            binding.HighlightCount++;
            if (binding.AnchorUtc is { } anchor)
            {
                offsetMs = Math.Max(
                    0L,
                    (long)Math.Round((highlightEvent.TriggerTime - anchor).TotalMilliseconds));
            }
        }

        return offsetMs is { } value
            ? highlightEvent with { RecordingSessionId = sessionId, RecordingOffsetMs = value }
            : highlightEvent with { RecordingSessionId = sessionId };
    }

    private static (PlatformType Platform, string RoomId) Normalize(PlatformType platform, string roomId)
        => (platform, (roomId ?? string.Empty).Trim().ToUpperInvariant());

    private sealed class Binding
    {
        public Binding(Guid sessionId)
        {
            SessionId = sessionId;
        }

        public Guid SessionId { get; }

        public DateTimeOffset? AnchorUtc { get; set; }

        public int HighlightCount { get; set; }
    }
}
