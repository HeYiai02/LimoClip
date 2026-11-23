using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

/// <summary>
/// 录制会话历史仓储。会话独立于直播间生命周期：删除直播间时外键把 LiveRoomId 置空，
/// 记录与本地文件保留；重新添加同一 (Platform, RoomId) 的直播间后可按平台房间号查回历史。
/// </summary>
public sealed class RecordingSessionRepository : IRecordingSessionRepository
{
    private const string SelectColumns =
        "Id, LiveRoomId, Platform, RoomId, RoomDisplayName, StreamTitle, Status, " +
        "VideoFilePath, DanmakuFilePath, StartedAt, EndedAt, DurationSeconds, FileSizeBytes, " +
        "DanmakuCount, HighlightCount, ErrorMessage";

    private readonly LiveMonitorDatabase _database;

    public RecordingSessionRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(RecordingSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RoomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.VideoFilePath);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RecordingSessions
                (Id, LiveRoomId, Platform, RoomId, RoomDisplayName, StreamTitle, Status,
                 VideoFilePath, DanmakuFilePath, StartedAt, EndedAt, DurationSeconds, FileSizeBytes,
                 DanmakuCount, HighlightCount, ErrorMessage)
            VALUES
                ($id, $liveRoomId, $platform, $roomId, $roomDisplayName, $streamTitle, $status,
                 $videoFilePath, $danmakuFilePath, $startedAt, $endedAt, $durationSeconds, $fileSizeBytes,
                 $danmakuCount, $highlightCount, $errorMessage);
            """;
        AddParameters(command, session);

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"录制会话无法写入（直播间不存在或 Id 冲突）：{session.Id}。", ex);
        }
    }

    public async Task UpdateAsync(RecordingSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RecordingSessions SET
                LiveRoomId = $liveRoomId,
                Platform = $platform,
                RoomId = $roomId,
                RoomDisplayName = $roomDisplayName,
                StreamTitle = $streamTitle,
                Status = $status,
                VideoFilePath = $videoFilePath,
                DanmakuFilePath = $danmakuFilePath,
                StartedAt = $startedAt,
                EndedAt = $endedAt,
                DurationSeconds = $durationSeconds,
                FileSizeBytes = $fileSizeBytes,
                DanmakuCount = $danmakuCount,
                HighlightCount = $highlightCount,
                ErrorMessage = $errorMessage
            WHERE Id = $id;
            """;
        AddParameters(command, session);

        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"录制会话不存在：{session.Id}。");
        }
    }

    public async Task<RecordingSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM RecordingSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<RecordingSession>> GetRecentAsync(
        int limit = 200,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<RecordingSession>();
        }

        var sessions = new List<RecordingSession>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM RecordingSessions
            ORDER BY StartedAt DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<RecordingSession>> GetByRoomAsync(
        PlatformType platform,
        string roomId,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        if (limit <= 0)
        {
            return Array.Empty<RecordingSession>();
        }

        var sessions = new List<RecordingSession>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM RecordingSessions
            WHERE Platform = $platform AND RoomId = $roomId
            ORDER BY StartedAt DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$platform", (int)platform);
        command.Parameters.AddWithValue("$roomId", roomId.Trim());
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<RecordingSession>> GetUnfinishedAsync(CancellationToken ct = default)
    {
        var sessions = new List<RecordingSession>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM RecordingSessions
            WHERE Status IN ({(int)RecordingStatus.Starting}, {(int)RecordingStatus.Recording}, {(int)RecordingStatus.Stopping})
            ORDER BY StartedAt;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RecordingSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(SqliteCommand command, RecordingSession session)
    {
        var id = session.Id == Guid.Empty ? Guid.NewGuid() : session.Id;
        var startedAt = session.StartedAt == default
            ? DateTime.UtcNow
            : session.StartedAt.ToUniversalTime();
        var endedAt = session.EndedAt?.ToUniversalTime();

        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        command.Parameters.AddWithValue(
            "$liveRoomId",
            session.LiveRoomId is { } liveRoomId && liveRoomId != Guid.Empty
                ? SqliteValues.ToDb(liveRoomId)
                : (object)DBNull.Value);
        command.Parameters.AddWithValue("$platform", (int)session.Platform);
        command.Parameters.AddWithValue("$roomId", session.RoomId.Trim());
        command.Parameters.AddWithValue("$roomDisplayName", session.RoomDisplayName);
        command.Parameters.AddWithValue("$streamTitle", session.StreamTitle);
        command.Parameters.AddWithValue("$status", (int)session.Status);
        command.Parameters.AddWithValue("$videoFilePath", session.VideoFilePath);
        command.Parameters.AddWithValue("$danmakuFilePath", session.DanmakuFilePath);
        command.Parameters.AddWithValue("$startedAt", SqliteValues.ToDb(startedAt));
        command.Parameters.AddWithValue(
            "$endedAt",
            endedAt is { } ended ? SqliteValues.ToDb(ended) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationSeconds", session.DurationSeconds);
        command.Parameters.AddWithValue("$fileSizeBytes", session.FileSizeBytes);
        command.Parameters.AddWithValue("$danmakuCount", session.DanmakuCount);
        command.Parameters.AddWithValue("$highlightCount", session.HighlightCount);
        command.Parameters.AddWithValue(
            "$errorMessage",
            string.IsNullOrWhiteSpace(session.ErrorMessage)
                ? (object)DBNull.Value
                : session.ErrorMessage);
    }

    private static RecordingSession ReadSession(SqliteDataReader reader)
    {
        var id = SqliteValues.ToGuid(reader.GetString(0));

        var platformValue = reader.GetInt32(2);
        if (!Enum.IsDefined(typeof(PlatformType), platformValue))
        {
            throw new InvalidDataException(
                $"RecordingSessions 中存在未知平台值：{platformValue}（Id={id}）。");
        }

        var statusValue = reader.GetInt32(6);
        if (!Enum.IsDefined(typeof(RecordingStatus), statusValue))
        {
            throw new InvalidDataException(
                $"RecordingSessions 中存在未知状态值：{statusValue}（Id={id}）。");
        }

        return new RecordingSession
        {
            Id = id,
            LiveRoomId = reader.IsDBNull(1) ? null : SqliteValues.ToGuid(reader.GetString(1)),
            Platform = (PlatformType)platformValue,
            RoomId = reader.GetString(3),
            RoomDisplayName = reader.GetString(4),
            StreamTitle = reader.GetString(5),
            Status = (RecordingStatus)statusValue,
            VideoFilePath = reader.GetString(7),
            DanmakuFilePath = reader.GetString(8),
            StartedAt = SqliteValues.ToDateTime(reader.GetString(9)),
            EndedAt = reader.IsDBNull(10) ? null : SqliteValues.ToDateTime(reader.GetString(10)),
            DurationSeconds = reader.GetDouble(11),
            FileSizeBytes = reader.GetInt64(12),
            DanmakuCount = reader.GetInt32(13),
            HighlightCount = reader.GetInt32(14),
            ErrorMessage = reader.IsDBNull(15) ? null : reader.GetString(15),
        };
    }
}
