using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Repositories;

public sealed class BlocklistRepository : IBlocklistRepository
{
    private readonly LiveMonitorDatabase _database;

    public BlocklistRepository(LiveMonitorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<DanmakuBlockKeyword>> GetAllAsync(CancellationToken ct = default)
    {
        var keywords = new List<DanmakuBlockKeyword>();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Keyword, IsRegex, CreatedAt FROM DanmakuBlockKeywords ORDER BY CreatedAt, Keyword;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keywords.Add(new DanmakuBlockKeyword
            {
                Id = SqliteValues.ToGuid(reader.GetString(0)),
                Keyword = reader.GetString(1),
                IsRegex = reader.GetInt32(2) != 0,
                CreatedAt = SqliteValues.ToDateTime(reader.GetString(3))
            });
        }

        return keywords;
    }

    public async Task AddAsync(DanmakuBlockKeyword keyword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword.Keyword);

        if (keyword.Id == Guid.Empty)
        {
            keyword.Id = Guid.NewGuid();
        }

        if (keyword.CreatedAt == default)
        {
            keyword.CreatedAt = DateTime.UtcNow;
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DanmakuBlockKeywords (Id, Keyword, IsRegex, CreatedAt)
            VALUES ($id, $keyword, $isRegex, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(keyword.Id));
        command.Parameters.AddWithValue("$keyword", keyword.Keyword);
        command.Parameters.AddWithValue("$isRegex", keyword.IsRegex ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", SqliteValues.ToDb(keyword.CreatedAt));

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"屏蔽词已存在：{keyword.Keyword}。", ex);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DanmakuBlockKeywords WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.ToDb(id));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string keyword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM DanmakuBlockKeywords WHERE Keyword = $keyword);";
        command.Parameters.AddWithValue("$keyword", keyword);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) != 0;
    }
}
