using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data;

public sealed class CredentialStore : ICredentialStore
{
    private readonly LiveMonitorDatabase _database;

    public CredentialStore(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<string?> GetCookieAsync(PlatformType platform, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EncryptedCookie, IsExpired
            FROM PlatformCredentials
            WHERE Platform = $platform
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$platform", (int)platform);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(1) != 0)
        {
            return null;
        }

        return DpapiCookieProtector.TryUnprotect(reader.GetString(0), out var cookie)
            ? cookie
            : null;
    }

    public async Task SaveAsync(
        PlatformType platform,
        string? accountName,
        string cookie,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);

        var encrypted = DpapiCookieProtector.Protect(cookie);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PlatformCredentials (Platform, AccountName, EncryptedCookie, UpdatedAt, IsExpired)
            VALUES ($platform, $accountName, $encryptedCookie, $updatedAt, 0)
            ON CONFLICT(Platform) DO UPDATE SET
                AccountName = excluded.AccountName,
                EncryptedCookie = excluded.EncryptedCookie,
                UpdatedAt = excluded.UpdatedAt,
                IsExpired = 0;
            """;
        command.Parameters.AddWithValue("$platform", (int)platform);
        command.Parameters.AddWithValue("$accountName", SqliteValues.NullableText(accountName));
        command.Parameters.AddWithValue("$encryptedCookie", encrypted);
        command.Parameters.AddWithValue("$updatedAt", SqliteValues.ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(PlatformType platform, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlatformCredentials WHERE Platform = $platform;";
        command.Parameters.AddWithValue("$platform", (int)platform);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkExpiredAsync(PlatformType platform, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE PlatformCredentials SET IsExpired = 1, UpdatedAt = $updatedAt WHERE Platform = $platform;";
        command.Parameters.AddWithValue("$updatedAt", SqliteValues.ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$platform", (int)platform);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsExpiredAsync(PlatformType platform, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IsExpired FROM PlatformCredentials WHERE Platform = $platform LIMIT 1;";
        command.Parameters.AddWithValue("$platform", (int)platform);
        var result = await command.ExecuteScalarAsync(ct);
        return result is not null and not DBNull && Convert.ToInt32(result) != 0;
    }
}
