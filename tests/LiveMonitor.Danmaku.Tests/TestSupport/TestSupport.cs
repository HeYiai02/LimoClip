using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Tests;

/// <summary>
/// 可手动推进的测试时钟；通过 <see cref="Reader"/> 注入到被测组件。
/// </summary>
public sealed class FakeClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset Now => _now;

    public Func<DateTimeOffset> Reader => () => _now;

    public void Advance(TimeSpan delta)
    {
        _now += delta;
    }
}

/// <summary>
/// 测试用弹幕消息工厂。Timestamp 默认 Unix 纪元，过滤器与检测器均使用注入时钟，不依赖该字段。
/// </summary>
public static class TestDanmaku
{
    public const string DefaultRoomId = "1001";
    public const string DefaultUserId = "1001";

    public static DanmakuMessage Create(
        string content,
        string uid = DefaultUserId,
        string roomId = DefaultRoomId,
        PlatformType platform = PlatformType.Bilibili,
        bool isGiftOrSuperChat = false,
        string senderName = "tester",
        bool isSticker = false,
        IReadOnlyList<DanmakuEmote>? emotes = null)
    {
        return new DanmakuMessage(
            platform,
            roomId,
            uid,
            senderName,
            content,
            DateTimeOffset.UnixEpoch,
            isGiftOrSuperChat,
            isSticker,
            emotes);
    }
}

/// <summary>
/// 内存版设置仓储，供 Danmaku 相关单测复用；语义与 SQLite 实现保持一致（键排序不敏感、串行化访问）。
/// </summary>
public sealed class InMemoryAppSettingsRepository : IAppSettingsRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>当前所有键值的快照副本，便于断言。</summary>
    public IReadOnlyDictionary<string, string> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_values, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>直接预置一个键值（模拟历史数据/非法数据）。</summary>
    public void Seed(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            _values[key] = value;
        }
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 内存版屏蔽词仓储，供 Danmaku 相关单测复用；语义与 SQLite 实现保持一致：
/// 关键词按精确匹配（区分大小写）去重、重复插入抛 <see cref="InvalidOperationException"/>。
/// </summary>
public sealed class InMemoryBlocklistRepository : IBlocklistRepository
{
    private readonly object _gate = new();
    private readonly List<DanmakuBlockKeyword> _keywords = new();

    /// <summary>当前词条快照副本，便于断言。</summary>
    public IReadOnlyList<DanmakuBlockKeyword> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _keywords.ToArray();
            }
        }
    }

    public Task<IReadOnlyList<DanmakuBlockKeyword>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public Task AddAsync(DanmakuBlockKeyword keyword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword.Keyword);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_keywords.Exists(existing => string.Equals(existing.Keyword, keyword.Keyword, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"屏蔽词已存在：{keyword.Keyword}。");
            }

            if (keyword.Id == Guid.Empty)
            {
                keyword.Id = Guid.NewGuid();
            }

            if (keyword.CreatedAt == default)
            {
                keyword.CreatedAt = DateTime.UtcNow;
            }

            _keywords.Add(keyword);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _keywords.RemoveAll(keyword => keyword.Id == id);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string keyword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_keywords.Exists(existing =>
                string.Equals(existing.Keyword, keyword, StringComparison.Ordinal)));
        }
    }
}
