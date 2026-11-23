using System.Globalization;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

/// <summary>
/// 高能事件落盘。应用重启后事件面板据此恢复历史。
/// Id 冲突时覆盖已有行（INSERT OR REPLACE），保证重复落盘/重放时幂等。
/// 时间与 <see cref="SqliteValues"/> 现有约定一致：库内为 UTC 的 "O" 格式文本。
/// </summary>
public sealed class HighlightEventRepository : IHighlightEventRepository
{
    private const string SelectColumns =
        "Id, Platform, RoomId, TriggerTime, CurrentRatePerSec, BaselineRatePerSec, TriggerReason, " +
        "RecordingSessionId, RecordingOffsetMs";

    private readonly LiveMonitorDatabase _database;

    public HighlightEventRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(HighlightEvent highlightEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highlightEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(highlightEvent.RoomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(highlightEvent.TriggerReason);

        var id = highlightEvent.Id == Guid.Empty ? Guid.NewGuid() : highlightEvent.Id;
        var triggerTime = highlightEvent.TriggerTime == default
            ? DateTimeOffset.UtcNow
            : highlightEvent.TriggerTime;

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO HighlightEvents
                (Id, Platform, RoomId, TriggerTime, CurrentRatePerSec, BaselineRatePerSec, TriggerReason,
                 RecordingSessionId, RecordingOffsetMs)
            VALUES
                ($id, $platform, $roomId, $triggerTime, $currentRate, $baselineRate, $triggerReason,
                 $recordingSessionId, $recordingOffsetMs);
            """;
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        command.Parameters.AddWithValue("$platform", (int)highlightEvent.Platform);
        command.Parameters.AddWithValue("$roomId", highlightEvent.RoomId);
        command.Parameters.AddWithValue("$triggerTime", ToDb(triggerTime));
        command.Parameters.AddWithValue("$currentRate", highlightEvent.CurrentRatePerSec);
        command.Parameters.AddWithValue("$baselineRate", highlightEvent.BaselineRatePerSec);
        command.Parameters.AddWithValue("$triggerReason", highlightEvent.TriggerReason);
        command.Parameters.AddWithValue(
            "$recordingSessionId",
            highlightEvent.RecordingSessionId is { } sessionId && sessionId != Guid.Empty
                ? SqliteValues.ToDb(sessionId)
                : (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$recordingOffsetMs",
            highlightEvent.RecordingOffsetMs is { } offsetMs ? offsetMs : (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<HighlightEvent>> GetRecentAsync(
        int limit = 200,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<HighlightEvent>();
        }

        var events = new List<HighlightEvent>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM HighlightEvents
            ORDER BY TriggerTime DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<HighlightEvent>> GetByRecordingSessionAsync(
        Guid sessionId,
        int limit = 500,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty || limit <= 0)
        {
            return Array.Empty<HighlightEvent>();
        }

        var events = new List<HighlightEvent>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM HighlightEvents
            WHERE RecordingSessionId = $sessionId
            ORDER BY COALESCE(RecordingOffsetMs, 0), TriggerTime
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", SqliteValues.ToDb(sessionId));
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM HighlightEvents;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static HighlightEvent ReadEvent(SqliteDataReader reader)
    {
        var id = SqliteValues.ToGuid(reader.GetString(0));
        var platformValue = reader.GetInt32(1);
        if (!Enum.IsDefined(typeof(PlatformType), platformValue))
        {
            throw new InvalidDataException(
                $"HighlightEvents 中存在未知平台值：{platformValue}（Id={id}）。");
        }

        return new HighlightEvent(
            (PlatformType)platformValue,
            reader.GetString(2),
            ToDateTimeOffset(reader.GetString(3)),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetString(6))
        {
            Id = id,
            RecordingSessionId = reader.IsDBNull(7)
                ? null
                : SqliteValues.ToGuid(reader.GetString(7)),
            RecordingOffsetMs = reader.IsDBNull(8) ? null : reader.GetInt64(8)
        };
    }

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ToDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
