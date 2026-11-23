using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiveMonitor.Data.Tests;

public sealed class RecordingRepositoryTests
{
    private static readonly DateTime BaseTime =
        new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Initialize_CreatesRecordingTablesAndIndexes_MigratesHighlightEventsColumns()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var connection = test.Database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('RecordingTargets', 'RecordingSessions');
            """;
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'index'
              AND name IN ('IX_RecordingSessions_StartedAt', 'IX_RecordingSessions_LiveRoomId',
                           'IX_RecordingSessions_PlatformRoom');
            """;
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        var columns = await ReadColumnNamesAsync(connection, "HighlightEvents");
        Assert.Contains("RecordingSessionId", columns);
        Assert.Contains("RecordingOffsetMs", columns);

        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Initialize_MigratesLegacyDatabase_AndKeepsExistingHighlightRows()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "LiveMonitor.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "livemonitor.db");

        try
        {
            await using (var legacy = new SqliteConnection($"Data Source={path}"))
            {
                await legacy.OpenAsync();
                await using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE HighlightEvents (
                        Id TEXT PRIMARY KEY,
                        Platform INTEGER NOT NULL,
                        RoomId TEXT NOT NULL,
                        TriggerTime TEXT NOT NULL,
                        CurrentRatePerSec REAL NOT NULL,
                        BaselineRatePerSec REAL NOT NULL,
                        TriggerReason TEXT NOT NULL
                    );
                    INSERT INTO HighlightEvents
                        (Id, Platform, RoomId, TriggerTime, CurrentRatePerSec,
                         BaselineRatePerSec, TriggerReason)
                    VALUES
                        ('11111111-1111-1111-1111-111111111111', 1, 'legacy-room',
                         '2026-11-20T04:00:00.0000000+00:00', 2.5, 1.0, 'legacy');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var database = new LiveMonitorDatabase(path);
            await database.InitializeAsync();

            await using var connection = database.OpenConnection();
            var columns = await ReadColumnNamesAsync(connection, "HighlightEvents");
            Assert.Contains("RecordingSessionId", columns);
            Assert.Contains("RecordingOffsetMs", columns);

            var events = await new HighlightEventRepository(database).GetRecentAsync();
            var migrated = Assert.Single(events);
            Assert.Equal("legacy-room", migrated.RoomId);
            Assert.Null(migrated.RecordingSessionId);
            Assert.Null(migrated.RecordingOffsetMs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Target_UpsertGetUpdateAndDelete_RoundTrips()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var targets = new RecordingTargetRepository(test.Database);

        var room = NewRoom("7001", "主播甲");
        await rooms.AddAsync(room);

        await targets.UpsertAsync(new RecordingTarget
        {
            LiveRoomId = room.Id,
            AutoRecordEnabled = true,
            CustomSaveDirectory = @"D:\录像\甲",
            UpdatedAt = BaseTime,
        });

        var loaded = Assert.IsType<RecordingTarget>(await targets.GetAsync(room.Id));
        Assert.Equal(room.Id, loaded.LiveRoomId);
        Assert.True(loaded.AutoRecordEnabled);
        Assert.Equal(@"D:\录像\甲", loaded.CustomSaveDirectory);
        Assert.Equal(BaseTime, loaded.UpdatedAt);

        // 同一主键再次 upsert 应覆盖而不是报错。
        await targets.UpsertAsync(new RecordingTarget
        {
            LiveRoomId = room.Id,
            AutoRecordEnabled = false,
            UpdatedAt = BaseTime.AddMinutes(5),
        });

        var all = await targets.GetAllAsync();
        var updated = Assert.Single(all);
        Assert.False(updated.AutoRecordEnabled);
        Assert.Null(updated.CustomSaveDirectory);

        await targets.DeleteAsync(room.Id);
        Assert.Null(await targets.GetAsync(room.Id));
        Assert.Empty(await targets.GetAllAsync());
    }

    [Fact]
    public async Task Target_IsRemoved_WhenRoomIsDeleted()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var targets = new RecordingTargetRepository(test.Database);

        var room = NewRoom("7002", "主播乙");
        await rooms.AddAsync(room);
        await targets.UpsertAsync(new RecordingTarget { LiveRoomId = room.Id, AutoRecordEnabled = true });

        await rooms.DeleteAsync(room.Id);

        Assert.Null(await targets.GetAsync(room.Id));
    }

    [Fact]
    public async Task Target_ForMissingRoom_ThrowsInvalidOperationException()
    {
        await using var test = await TestDatabase.CreateAsync();
        var targets = new RecordingTargetRepository(test.Database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => targets.UpsertAsync(
            new RecordingTarget { LiveRoomId = Guid.NewGuid(), AutoRecordEnabled = true }));
    }

    [Fact]
    public async Task Session_AddAndGet_RoundTripsAllFields()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var sessions = new RecordingSessionRepository(test.Database);

        var room = NewRoom("7003", "主播丙");
        await rooms.AddAsync(room);

        var session = new RecordingSession
        {
            Id = Guid.NewGuid(),
            LiveRoomId = room.Id,
            Platform = PlatformType.Twitch,
            RoomId = "7003",
            RoomDisplayName = "#1 主播丙",
            StreamTitle = "今晚打排位",
            Status = RecordingStatus.Recording,
            VideoFilePath = @"D:\录像\20261123_080000_#1_丙_7003.mkv",
            DanmakuFilePath = @"D:\录像\20261123_080000_#1_丙_7003.jsonl",
            StartedAt = BaseTime,
            EndedAt = null,
            DurationSeconds = 0,
            FileSizeBytes = 123456,
            DanmakuCount = 42,
            HighlightCount = 3,
        };
        await sessions.AddAsync(session);

        var loaded = Assert.IsType<RecordingSession>(await sessions.GetByIdAsync(session.Id));
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(room.Id, loaded.LiveRoomId);
        Assert.Equal(PlatformType.Twitch, loaded.Platform);
        Assert.Equal("7003", loaded.RoomId);
        Assert.Equal("#1 主播丙", loaded.RoomDisplayName);
        Assert.Equal("今晚打排位", loaded.StreamTitle);
        Assert.Equal(RecordingStatus.Recording, loaded.Status);
        Assert.Equal(session.VideoFilePath, loaded.VideoFilePath);
        Assert.Equal(session.DanmakuFilePath, loaded.DanmakuFilePath);
        Assert.Equal(BaseTime, loaded.StartedAt);
        Assert.Null(loaded.EndedAt);
        Assert.Equal(123456, loaded.FileSizeBytes);
        Assert.Equal(42, loaded.DanmakuCount);
        Assert.Equal(3, loaded.HighlightCount);
        Assert.Null(loaded.ErrorMessage);
    }

    [Fact]
    public async Task Session_Update_PersistsStatusEndAndCounters()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var sessions = new RecordingSessionRepository(test.Database);

        var room = NewRoom("7004", "主播丁");
        await rooms.AddAsync(room);

        var session = NewSession(room, "7004", BaseTime);
        await sessions.AddAsync(session);

        session.Status = RecordingStatus.Completed;
        session.EndedAt = BaseTime.AddHours(2);
        session.DurationSeconds = 7200;
        session.FileSizeBytes = 8_000_000_000;
        session.HighlightCount = 17;
        session.ErrorMessage = null;
        await sessions.UpdateAsync(session);

        var loaded = Assert.IsType<RecordingSession>(await sessions.GetByIdAsync(session.Id));
        Assert.Equal(RecordingStatus.Completed, loaded.Status);
        Assert.NotNull(loaded.EndedAt);
        Assert.Equal(7200, loaded.DurationSeconds);
        Assert.Equal(8_000_000_000, loaded.FileSizeBytes);
        Assert.Equal(17, loaded.HighlightCount);
    }

    [Fact]
    public async Task Session_Update_MissingRow_ThrowsKeyNotFoundException()
    {
        await using var test = await TestDatabase.CreateAsync();
        var sessions = new RecordingSessionRepository(test.Database);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sessions.UpdateAsync(NewSession(Guid.NewGuid(), PlatformType.Bilibili, "missing", BaseTime)));
    }

    [Fact]
    public async Task Session_DeleteRoom_KeepsHistory_AndClearsLiveRoomId()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var sessions = new RecordingSessionRepository(test.Database);

        var room = NewRoom("7005", "主播戊");
        await rooms.AddAsync(room);

        var session = NewSession(room, "7005", BaseTime);
        await sessions.AddAsync(session);

        await rooms.DeleteAsync(room.Id);

        var loaded = Assert.IsType<RecordingSession>(await sessions.GetByIdAsync(session.Id));
        Assert.Null(loaded.LiveRoomId);
        Assert.Equal("7005", loaded.RoomId);
        Assert.Equal(session.VideoFilePath, loaded.VideoFilePath);

        // 重新添加同一平台房间后，历史仍可按房间号查回。
        var readded = NewRoom("7005", "主播戊");
        await rooms.AddAsync(readded);
        var byRoom = await sessions.GetByRoomAsync(PlatformType.Bilibili, "7005");
        var history = Assert.Single(byRoom);
        Assert.Equal(session.Id, history.Id);
        Assert.Null(history.LiveRoomId);
    }

    [Fact]
    public async Task Session_QueryOrderingLimitAndRoomFilter()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var sessions = new RecordingSessionRepository(test.Database);

        var roomA = NewRoom("8001", "A");
        var roomB = NewRoom("8002", "B");
        await rooms.AddAsync(roomA);
        await rooms.AddAsync(roomB);

        var oldest = NewSession(roomA, "8001", BaseTime);
        var newest = NewSession(roomA, "8001", BaseTime.AddHours(2));
        var other = NewSession(roomB, "8002", BaseTime.AddHours(1));

        await sessions.AddAsync(oldest);
        await sessions.AddAsync(newest);
        await sessions.AddAsync(other);

        var recent = await sessions.GetRecentAsync();
        Assert.Equal(
            new[] { newest.Id, other.Id, oldest.Id },
            recent.Select(item => item.Id).ToArray());

        Assert.Equal(2, (await sessions.GetRecentAsync(2)).Count);
        Assert.Empty(await sessions.GetRecentAsync(0));

        var byRoom = await sessions.GetByRoomAsync(PlatformType.Bilibili, "8001");
        Assert.Equal(new[] { newest.Id, oldest.Id }, byRoom.Select(item => item.Id).ToArray());
        Assert.Empty(await sessions.GetByRoomAsync(PlatformType.Twitch, "8001"));
    }

    [Fact]
    public async Task Session_GetUnfinished_ReturnsOnlyActiveStatuses()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var sessions = new RecordingSessionRepository(test.Database);

        var room = NewRoom("8003", "C");
        await rooms.AddAsync(room);

        var starting = NewSession(room, "8003", BaseTime);
        starting.Status = RecordingStatus.Starting;
        var recording = NewSession(room, "8003", BaseTime.AddMinutes(1));
        recording.Status = RecordingStatus.Recording;
        var stopping = NewSession(room, "8003", BaseTime.AddMinutes(2));
        stopping.Status = RecordingStatus.Stopping;
        var completed = NewSession(room, "8003", BaseTime.AddMinutes(3));
        completed.Status = RecordingStatus.Completed;
        var failed = NewSession(room, "8003", BaseTime.AddMinutes(4));
        failed.Status = RecordingStatus.Failed;

        foreach (var session in new[] { starting, recording, stopping, completed, failed })
        {
            await sessions.AddAsync(session);
        }

        var unfinished = await sessions.GetUnfinishedAsync();
        Assert.Equal(
            new[] { starting.Id, recording.Id, stopping.Id },
            unfinished.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Session_WithMissingLiveRoom_ThrowsInvalidOperationException()
    {
        await using var test = await TestDatabase.CreateAsync();
        var sessions = new RecordingSessionRepository(test.Database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.AddAsync(NewSession(Guid.NewGuid(), PlatformType.Bilibili, "7006", BaseTime)));
    }

    [Fact]
    public async Task Session_UnknownStatusOrPlatform_ThrowsInvalidDataException()
    {
        await using var test = await TestDatabase.CreateAsync();
        var sessions = new RecordingSessionRepository(test.Database);

        await using (var connection = test.Database.OpenConnection())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO RecordingSessions
                    (Id, LiveRoomId, Platform, RoomId, RoomDisplayName, StreamTitle, Status,
                     VideoFilePath, DanmakuFilePath, StartedAt, EndedAt, DurationSeconds,
                     FileSizeBytes, DanmakuCount, HighlightCount, ErrorMessage)
                VALUES
                    ($id, NULL, 999, 'bad-platform', 'bad', '', 0, 'x.mkv', 'x.jsonl',
                     '2026-11-23T00:00:00.0000000+00:00', NULL, 0, 0, 0, 0, NULL);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => sessions.GetRecentAsync(10));

        await using (var connection = test.Database.OpenConnection())
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM RecordingSessions;";
            await cleanup.ExecuteNonQueryAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO RecordingSessions
                    (Id, LiveRoomId, Platform, RoomId, RoomDisplayName, StreamTitle, Status,
                     VideoFilePath, DanmakuFilePath, StartedAt, EndedAt, DurationSeconds,
                     FileSizeBytes, DanmakuCount, HighlightCount, ErrorMessage)
                VALUES
                    ($id, NULL, 1, 'bad-status', 'bad', '', 9, 'x.mkv', 'x.jsonl',
                     '2026-11-23T00:00:00.0000000+00:00', NULL, 0, 0, 0, 0, NULL);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => sessions.GetRecentAsync(10));
    }

    [Fact]
    public async Task HighlightEvent_WithRecordingBinding_RoundTrips()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);
        var sessionId = Guid.NewGuid();

        await repository.AddAsync(new HighlightEvent(
            PlatformType.Bilibili,
            "9001",
            new DateTimeOffset(BaseTime),
            12.5,
            3.0,
            "弹幕密度超过阈值")
        {
            RecordingSessionId = sessionId,
            RecordingOffsetMs = 125_000,
        });

        var loaded = Assert.Single(await repository.GetRecentAsync());
        Assert.Equal(sessionId, loaded.RecordingSessionId);
        Assert.Equal(125_000, loaded.RecordingOffsetMs);
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static LiveRoom NewRoom(string roomId, string alias) => new()
    {
        Platform = PlatformType.Bilibili,
        RoomId = roomId,
        RawUrl = $"https://live.bilibili.com/{roomId}",
        Alias = alias,
    };

    private static RecordingSession NewSession(LiveRoom room, string roomId, DateTime startedAt) => new()
    {
        LiveRoomId = room.Id,
        Platform = room.Platform,
        RoomId = roomId,
        RoomDisplayName = $"#{room.DisplayNumber} {room.Alias}",
        VideoFilePath = $@"D:\录像\{roomId}.mkv",
        DanmakuFilePath = $@"D:\录像\{roomId}.jsonl",
        StartedAt = startedAt,
        Status = RecordingStatus.Starting,
    };

    private static RecordingSession NewSession(
        Guid liveRoomId,
        PlatformType platform,
        string roomId,
        DateTime startedAt) => new()
    {
        LiveRoomId = liveRoomId,
        Platform = platform,
        RoomId = roomId,
        RoomDisplayName = "missing",
        VideoFilePath = $@"D:\录像\{roomId}.mkv",
        DanmakuFilePath = $@"D:\录像\{roomId}.jsonl",
        StartedAt = startedAt,
        Status = RecordingStatus.Starting,
    };
}
