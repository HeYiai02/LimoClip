using LiveMonitor.Core.Interfaces;

namespace LiveMonitor.Data.Repositories;

public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private readonly LiveMonitorDatabase _database;

    public AppSettingsRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AppSettings ORDER BY Key;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", SqliteValues.ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct);
    }
}
