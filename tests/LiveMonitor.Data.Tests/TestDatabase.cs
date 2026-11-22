using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TestDatabase(string directory, LiveMonitorDatabase database)
    {
        _directory = directory;
        Database = database;
    }

    public LiveMonitorDatabase Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "LiveMonitor.Data.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var database = new LiveMonitorDatabase(Path.Combine(directory, "livemonitor.db"));
        await database.InitializeAsync();
        return new TestDatabase(directory, database);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // WAL 文件偶尔仍被测试宿主占用，清理失败不影响断言结果。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
