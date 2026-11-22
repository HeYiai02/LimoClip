using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

public sealed class RoomRepository : IRoomRepository
{
    private const string SelectColumns =
        "Id, DisplayNumber, Platform, RoomId, RawUrl, Alias, GroupTag, CreatedAt";

    private readonly LiveMonitorDatabase _database;

    public RoomRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<LiveRoom>> GetAllAsync(CancellationToken ct = default)
    {
        var rooms = new List<LiveRoom>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM LiveRooms ORDER BY DisplayNumber;";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rooms.Add(ReadRoom(reader));
        }

        return rooms;
    }

    public async Task<LiveRoom?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM LiveRooms WHERE Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRoom(reader) : null;
    }

    public async Task<bool> ExistsAsync(PlatformType platform, string roomId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM LiveRooms WHERE Platform = $platform AND RoomId = $roomId);";
        command.Parameters.AddWithValue("$platform", (int)platform);
        command.Parameters.AddWithValue("$roomId", roomId);

        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) != 0;
    }

    public async Task<int> GetNextDisplayNumberAsync(CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        return await GetNextDisplayNumberAsync(connection, transaction: null, ct);
    }

    public async Task AddAsync(LiveRoom room, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (room.Id == Guid.Empty)
        {
            room.Id = Guid.NewGuid();
        }

        if (room.CreatedAt == default)
        {
            room.CreatedAt = DateTime.UtcNow;
        }

        await using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (room.DisplayNumber <= 0)
        {
            room.DisplayNumber = await GetNextDisplayNumberAsync(connection, transaction, ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LiveRooms (Id, DisplayNumber, Platform, RoomId, RawUrl, Alias, GroupTag, CreatedAt)
                VALUES ($id, $displayNumber, $platform, $roomId, $rawUrl, $alias, $groupTag, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", SqliteValues.ToDb(room.Id));
            command.Parameters.AddWithValue("$displayNumber", room.DisplayNumber);
            command.Parameters.AddWithValue("$platform", (int)room.Platform);
            command.Parameters.AddWithValue("$roomId", room.RoomId);
            command.Parameters.AddWithValue("$rawUrl", room.RawUrl);
            command.Parameters.AddWithValue("$alias", room.Alias);
            command.Parameters.AddWithValue("$groupTag", SqliteValues.NullableText(room.GroupTag));
            command.Parameters.AddWithValue("$createdAt", SqliteValues.ToDb(room.CreatedAt));

            await command.ExecuteNonQueryAsync(ct);
            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"直播间已存在或展示序号冲突：{room.Platform}/{room.RoomId}。", ex);
        }
    }

    public async Task UpdateAsync(LiveRoom room, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.DisplayNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(room), "展示序号必须为正整数。");
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LiveRooms
            SET DisplayNumber = $displayNumber,
                Platform = $platform,
                RoomId = $roomId,
                RawUrl = $rawUrl,
                Alias = $alias,
                GroupTag = $groupTag,
                CreatedAt = $createdAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$displayNumber", room.DisplayNumber);
        command.Parameters.AddWithValue("$platform", (int)room.Platform);
        command.Parameters.AddWithValue("$roomId", room.RoomId);
        command.Parameters.AddWithValue("$rawUrl", room.RawUrl);
        command.Parameters.AddWithValue("$alias", room.Alias);
        command.Parameters.AddWithValue("$groupTag", SqliteValues.NullableText(room.GroupTag));
        command.Parameters.AddWithValue("$createdAt", SqliteValues.ToDb(room.CreatedAt));
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(room.Id));

        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"直播间不存在：{room.Id}。");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LiveRooms WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetNextDisplayNumberAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(DisplayNumber), 0) + 1 FROM LiveRooms;";
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static LiveRoom ReadRoom(SqliteDataReader reader) => new()
    {
        Id = SqliteValues.ToGuid(reader.GetString(0)),
        DisplayNumber = reader.GetInt32(1),
        Platform = (PlatformType)reader.GetInt32(2),
        RoomId = reader.GetString(3),
        RawUrl = reader.GetString(4),
        Alias = reader.GetString(5),
        GroupTag = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = SqliteValues.ToDateTime(reader.GetString(7))
    };
}
