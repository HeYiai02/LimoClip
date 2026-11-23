using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

/// <summary>
/// 录制偏好仓储。每个直播间至多一行，按 LiveRoomId upsert。
/// 直播间被删除时由外键级联清理；本表只存配置，不承载历史。
/// </summary>
public sealed class RecordingTargetRepository : IRecordingTargetRepository
{
    private const string SelectColumns =
        "LiveRoomId, AutoRecordEnabled, CustomSaveDirectory, UpdatedAt";

    private readonly LiveMonitorDatabase _database;

    public RecordingTargetRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<RecordingTarget>> GetAllAsync(CancellationToken ct = default)
    {
        var targets = new List<RecordingTarget>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM RecordingTargets ORDER BY UpdatedAt;";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            targets.Add(ReadTarget(reader));
        }

        return targets;
    }

    public async Task<RecordingTarget?> GetAsync(Guid liveRoomId, CancellationToken ct = default)
    {
        if (liveRoomId == Guid.Empty)
        {
            return null;
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM RecordingTargets WHERE LiveRoomId = $liveRoomId;";
        command.Parameters.AddWithValue("$liveRoomId", SqliteValues.ToDb(liveRoomId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTarget(reader) : null;
    }

    public async Task UpsertAsync(RecordingTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.LiveRoomId == Guid.Empty)
        {
            throw new ArgumentException("录制目标必须关联直播间 Id。", nameof(target));
        }

        var updatedAt = target.UpdatedAt == default ? DateTime.UtcNow : target.UpdatedAt;

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RecordingTargets (LiveRoomId, AutoRecordEnabled, CustomSaveDirectory, UpdatedAt)
            VALUES ($liveRoomId, $autoRecordEnabled, $customSaveDirectory, $updatedAt)
            ON CONFLICT(LiveRoomId) DO UPDATE SET
                AutoRecordEnabled = excluded.AutoRecordEnabled,
                CustomSaveDirectory = excluded.CustomSaveDirectory,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$liveRoomId", SqliteValues.ToDb(target.LiveRoomId));
        command.Parameters.AddWithValue("$autoRecordEnabled", target.AutoRecordEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$customSaveDirectory",
            string.IsNullOrWhiteSpace(target.CustomSaveDirectory)
                ? (object)DBNull.Value
                : target.CustomSaveDirectory.Trim());
        command.Parameters.AddWithValue("$updatedAt", SqliteValues.ToDb(updatedAt));

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"录制目标关联的直播间不存在：{target.LiveRoomId}。", ex);
        }
    }

    public async Task DeleteAsync(Guid liveRoomId, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RecordingTargets WHERE LiveRoomId = $liveRoomId;";
        command.Parameters.AddWithValue("$liveRoomId", SqliteValues.ToDb(liveRoomId));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static RecordingTarget ReadTarget(SqliteDataReader reader) => new()
    {
        LiveRoomId = SqliteValues.ToGuid(reader.GetString(0)),
        AutoRecordEnabled = reader.GetInt32(1) != 0,
        CustomSaveDirectory = reader.IsDBNull(2) ? null : reader.GetString(2),
        UpdatedAt = SqliteValues.ToDateTime(reader.GetString(3)),
    };
}
