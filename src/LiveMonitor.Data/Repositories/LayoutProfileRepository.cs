using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

public sealed class LayoutProfileRepository : ILayoutProfileRepository
{
    private readonly LiveMonitorDatabase _database;

    public LayoutProfileRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<LayoutProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var profiles = new List<LayoutProfile>();
        var byId = new Dictionary<Guid, LayoutProfile>();

        await using var connection = _database.OpenConnection();

        await using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.CommandText =
                "SELECT Id, Name, IsCustomLayout, DockLayoutJson FROM LayoutProfiles ORDER BY Name;";
            await using var reader = await profileCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var profile = ReadProfile(reader);
                profiles.Add(profile);
                byId[profile.Id] = profile;
            }
        }

        await using (var slotCommand = connection.CreateCommand())
        {
            slotCommand.CommandText = """
                SELECT Id, LayoutProfileId, LiveRoomId, PaneId, DefaultMuted, DefaultDanmakuVisible, IsFocusSlot, PreferredQualityId
                FROM LayoutSlots
                ORDER BY PaneId;
                """;
            await using var reader = await slotCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var slot = ReadSlot(reader);
                if (byId.TryGetValue(slot.LayoutProfileId, out var profile))
                {
                    profile.Slots.Add(slot);
                }
            }
        }

        return profiles;
    }

    public async Task<LayoutProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        LayoutProfile? profile = null;

        await using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.CommandText =
                "SELECT Id, Name, IsCustomLayout, DockLayoutJson FROM LayoutProfiles WHERE Id = $id LIMIT 1;";
            profileCommand.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
            await using var reader = await profileCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            profile = ReadProfile(reader);
        }

        await using (var slotCommand = connection.CreateCommand())
        {
            slotCommand.CommandText = """
                SELECT Id, LayoutProfileId, LiveRoomId, PaneId, DefaultMuted, DefaultDanmakuVisible, IsFocusSlot, PreferredQualityId
                FROM LayoutSlots
                WHERE LayoutProfileId = $id
                ORDER BY PaneId;
                """;
            slotCommand.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
            await using var reader = await slotCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                profile.Slots.Add(ReadSlot(reader));
            }
        }

        return profile;
    }

    public async Task SaveAsync(LayoutProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);

        if (profile.Id == Guid.Empty)
        {
            profile.Id = Guid.NewGuid();
        }

        profile.Slots ??= new List<LayoutSlot>();

        var paneIds = new HashSet<string>(StringComparer.Ordinal);
        var roomIds = new HashSet<Guid>();
        foreach (var slot in profile.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.PaneId))
            {
                throw new InvalidOperationException("方案槽位的 PaneId 不能为空。");
            }

            if (!paneIds.Add(slot.PaneId))
            {
                throw new InvalidOperationException($"方案内 PaneId 重复：{slot.PaneId}。");
            }

            if (!roomIds.Add(slot.LiveRoomId))
            {
                throw new InvalidOperationException($"方案内直播间重复：{slot.LiveRoomId}。");
            }
        }

        await using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO LayoutProfiles (Id, Name, IsCustomLayout, DockLayoutJson)
                    VALUES ($id, $name, $isCustomLayout, $dockLayoutJson)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        IsCustomLayout = excluded.IsCustomLayout,
                        DockLayoutJson = excluded.DockLayoutJson;
                    """;
                upsert.Parameters.AddWithValue("$id", SqliteValues.ToDb(profile.Id));
                upsert.Parameters.AddWithValue("$name", profile.Name);
                upsert.Parameters.AddWithValue("$isCustomLayout", profile.IsCustomLayout ? 1 : 0);
                upsert.Parameters.AddWithValue("$dockLayoutJson", SqliteValues.NullableText(profile.DockLayoutJson));
                await upsert.ExecuteNonQueryAsync(ct);
            }

            await using (var deleteSlots = connection.CreateCommand())
            {
                deleteSlots.Transaction = transaction;
                deleteSlots.CommandText = "DELETE FROM LayoutSlots WHERE LayoutProfileId = $id;";
                deleteSlots.Parameters.AddWithValue("$id", SqliteValues.ToDb(profile.Id));
                await deleteSlots.ExecuteNonQueryAsync(ct);
            }

            foreach (var slot in profile.Slots)
            {
                if (slot.Id == Guid.Empty)
                {
                    slot.Id = Guid.NewGuid();
                }

                slot.LayoutProfileId = profile.Id;

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO LayoutSlots
                        (Id, LayoutProfileId, LiveRoomId, PaneId, DefaultMuted, DefaultDanmakuVisible, IsFocusSlot, PreferredQualityId)
                    VALUES
                        ($id, $profileId, $roomId, $paneId, $defaultMuted, $defaultDanmakuVisible, $isFocusSlot, $preferredQualityId);
                    """;
                insert.Parameters.AddWithValue("$id", SqliteValues.ToDb(slot.Id));
                insert.Parameters.AddWithValue("$profileId", SqliteValues.ToDb(profile.Id));
                insert.Parameters.AddWithValue("$roomId", SqliteValues.ToDb(slot.LiveRoomId));
                insert.Parameters.AddWithValue("$paneId", slot.PaneId);
                insert.Parameters.AddWithValue("$defaultMuted", slot.DefaultMuted ? 1 : 0);
                insert.Parameters.AddWithValue("$defaultDanmakuVisible", slot.DefaultDanmakuVisible ? 1 : 0);
                insert.Parameters.AddWithValue("$isFocusSlot", slot.IsFocusSlot ? 1 : 0);
                insert.Parameters.AddWithValue("$preferredQualityId", SqliteValues.NullableText(slot.PreferredQualityId));
                await insert.ExecuteNonQueryAsync(ct);
            }

            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"方案槽位引用了不存在的直播间或平台数据不合法：{profile.Name}。", ex);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LayoutProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static LayoutProfile ReadProfile(SqliteDataReader reader) => new()
    {
        Id = SqliteValues.ToGuid(reader.GetString(0)),
        Name = reader.GetString(1),
        IsCustomLayout = reader.GetInt32(2) != 0,
        DockLayoutJson = reader.IsDBNull(3) ? null : reader.GetString(3)
    };

    private static LayoutSlot ReadSlot(SqliteDataReader reader) => new()
    {
        Id = SqliteValues.ToGuid(reader.GetString(0)),
        LayoutProfileId = SqliteValues.ToGuid(reader.GetString(1)),
        LiveRoomId = SqliteValues.ToGuid(reader.GetString(2)),
        PaneId = reader.GetString(3),
        DefaultMuted = reader.GetInt32(4) != 0,
        DefaultDanmakuVisible = reader.GetInt32(5) != 0,
        IsFocusSlot = reader.GetInt32(6) != 0,
        PreferredQualityId = reader.IsDBNull(7) ? null : reader.GetString(7)
    };
}
