using Microsoft.Data.Sqlite;

namespace LiveMonitor.Data;

/// <summary>
/// SQLite 数据库入口：负责连接串、WAL、外键与建表。
/// 每个仓储操作独立打开连接，避免长事务占用。
/// </summary>
public sealed class LiveMonitorDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS LiveRooms (
            Id TEXT PRIMARY KEY,
            DisplayNumber INTEGER NOT NULL UNIQUE,
            Platform INTEGER NOT NULL,
            RoomId TEXT NOT NULL,
            RawUrl TEXT NOT NULL,
            Alias TEXT NOT NULL,
            GroupTag TEXT,
            CreatedAt TEXT NOT NULL,
            UNIQUE(Platform, RoomId)
        );

        CREATE TABLE IF NOT EXISTS LayoutProfiles (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            IsCustomLayout INTEGER NOT NULL DEFAULT 0,
            DockLayoutJson TEXT
        );

        CREATE TABLE IF NOT EXISTS LayoutSlots (
            Id TEXT PRIMARY KEY,
            LayoutProfileId TEXT NOT NULL,
            LiveRoomId TEXT NOT NULL,
            PaneId TEXT NOT NULL,
            DefaultMuted INTEGER NOT NULL DEFAULT 1,
            DefaultDanmakuVisible INTEGER NOT NULL DEFAULT 0,
            IsFocusSlot INTEGER NOT NULL DEFAULT 0,
            PreferredQualityId TEXT,
            FOREIGN KEY (LayoutProfileId) REFERENCES LayoutProfiles(Id) ON DELETE CASCADE,
            FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE CASCADE,
            UNIQUE(LayoutProfileId, LiveRoomId)
        );

        CREATE TABLE IF NOT EXISTS PlatformCredentials (
            Platform INTEGER PRIMARY KEY,
            AccountName TEXT,
            EncryptedCookie TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            IsExpired INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS DanmakuBlockKeywords (
            Id TEXT PRIMARY KEY,
            Keyword TEXT NOT NULL UNIQUE,
            IsRegex INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AppSettings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS HighlightEvents (
            Id TEXT PRIMARY KEY,
            Platform INTEGER NOT NULL,
            RoomId TEXT NOT NULL,
            TriggerTime TEXT NOT NULL,
            CurrentRatePerSec REAL NOT NULL,
            BaselineRatePerSec REAL NOT NULL,
            TriggerReason TEXT NOT NULL,
            RecordingSessionId TEXT,
            RecordingOffsetMs INTEGER
        );

        CREATE TABLE IF NOT EXISTS RecordingTargets (
            LiveRoomId TEXT PRIMARY KEY,
            AutoRecordEnabled INTEGER NOT NULL DEFAULT 0,
            CustomSaveDirectory TEXT,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS RecordingSessions (
            Id TEXT PRIMARY KEY,
            LiveRoomId TEXT,
            Platform INTEGER NOT NULL,
            RoomId TEXT NOT NULL,
            RoomDisplayName TEXT NOT NULL,
            StreamTitle TEXT NOT NULL DEFAULT '',
            Status INTEGER NOT NULL DEFAULT 0,
            VideoFilePath TEXT NOT NULL,
            DanmakuFilePath TEXT NOT NULL,
            StartedAt TEXT NOT NULL,
            EndedAt TEXT,
            DurationSeconds REAL NOT NULL DEFAULT 0,
            FileSizeBytes INTEGER NOT NULL DEFAULT 0,
            DanmakuCount INTEGER NOT NULL DEFAULT 0,
            HighlightCount INTEGER NOT NULL DEFAULT 0,
            ErrorMessage TEXT,
            FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS IX_LayoutSlots_ProfileId ON LayoutSlots(LayoutProfileId);
        CREATE INDEX IF NOT EXISTS IX_LayoutSlots_LiveRoomId ON LayoutSlots(LiveRoomId);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LayoutSlots_ProfileRoom ON LayoutSlots(LayoutProfileId, LiveRoomId);
        CREATE INDEX IF NOT EXISTS IX_HighlightEvents_TriggerTime ON HighlightEvents(TriggerTime DESC);
        CREATE INDEX IF NOT EXISTS IX_RecordingSessions_StartedAt ON RecordingSessions(StartedAt DESC);
        CREATE INDEX IF NOT EXISTS IX_RecordingSessions_LiveRoomId ON RecordingSessions(LiveRoomId);
        CREATE INDEX IF NOT EXISTS IX_RecordingSessions_PlatformRoom ON RecordingSessions(Platform, RoomId);
        """;

    private readonly string _connectionString;

    public LiveMonitorDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveMonitor",
        "livemonitor.db");

    /// <summary>创建目录、切换 WAL 并执行幂等建表脚本。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = OpenConnection();

        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteScalarAsync(ct);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            await schema.ExecuteNonQueryAsync(ct);
        }

        await EnsureColumnAsync(connection, "LayoutProfiles", "IsCustomLayout",
            "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "LayoutSlots", "DefaultDanmakuVisible",
            "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "HighlightEvents", "RecordingSessionId",
            "TEXT", ct).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "HighlightEvents", "RecordingOffsetMs",
            "INTEGER", ct).ConfigureAwait(false);

        // 旧库的 HighlightEvents 在补列之前不能建该索引，因此放在 EnsureColumn 之后。
        await using (var sessionIndex = connection.CreateCommand())
        {
            sessionIndex.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_HighlightEvents_RecordingSessionId " +
                "ON HighlightEvents(RecordingSessionId);";
            await sessionIndex.ExecuteNonQueryAsync(ct);
        }

        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = 4;";
            await version.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    /// <summary>打开连接，并强制每个连接启用外键与忙等待。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
