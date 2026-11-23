using System.Globalization;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Data.Repositories;
using Xunit;

namespace LiveMonitor.Data.Tests;

public sealed class HighlightEventRepositoryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Initialize_CreatesHighlightEventsTable_AndTriggerTimeIndex()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var connection = test.Database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HighlightEvents';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_HighlightEvents_TriggerTime';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AddAsync_ThenGetRecent_RoundTripsAllFields()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        var id = Guid.NewGuid();
        var triggerTime = new DateTimeOffset(2026, 11, 20, 21, 30, 45, 123, TimeSpan.FromHours(8))
            .AddTicks(4567);
        var highlightEvent = new HighlightEvent(
            PlatformType.Douyin,
            "room-42",
            triggerTime,
            18.75,
            3.5,
            "弹幕速率 18.75/s 超过阈值 10/s")
        {
            Id = id
        };

        await repository.AddAsync(highlightEvent);

        var reloaded = Assert.Single(await repository.GetRecentAsync());
        Assert.Equal(id, reloaded.Id);
        Assert.Equal(PlatformType.Douyin, reloaded.Platform);
        Assert.Equal("room-42", reloaded.RoomId);
        Assert.Equal(triggerTime, reloaded.TriggerTime);
        Assert.Equal(TimeSpan.Zero, reloaded.TriggerTime.Offset);
        Assert.Equal(18.75, reloaded.CurrentRatePerSec);
        Assert.Equal(3.5, reloaded.BaselineRatePerSec);
        Assert.Equal("弹幕速率 18.75/s 超过阈值 10/s", reloaded.TriggerReason);

        // 库内按现有约定存 UTC 的 "O" 格式文本。
        await using var connection = test.Database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TriggerTime FROM HighlightEvents WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var stored = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Equal(
            triggerTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            stored);
    }

    [Fact]
    public async Task GetRecent_MapsEveryPlatformType()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        var platforms = Enum.GetValues<PlatformType>();
        for (var i = 0; i < platforms.Length; i++)
        {
            await repository.AddAsync(new HighlightEvent(
                platforms[i],
                $"room-{platforms[i]}",
                BaseTime.AddMinutes(i),
                1.5 + i,
                0.5 + i,
                $"reason-{platforms[i]}"));
        }

        var recent = await repository.GetRecentAsync();
        Assert.Equal(platforms.Length, recent.Count);

        var byPlatform = recent.ToDictionary(item => item.Platform);
        Assert.Equal(platforms.Length, byPlatform.Count);
        foreach (var platform in platforms)
        {
            var reloaded = byPlatform[platform];
            Assert.Equal(platform, reloaded.Platform);
            Assert.Equal($"room-{platform}", reloaded.RoomId);
            Assert.Equal($"reason-{platform}", reloaded.TriggerReason);
        }
    }

    [Fact]
    public async Task GetRecent_OrdersByTriggerTimeDescending_AcrossOffsets()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        var oldest = new HighlightEvent(
            PlatformType.Bilibili, "oldest",
            new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero), 1.0, 0.5, "oldest");
        var middle = new HighlightEvent(
            PlatformType.Douyu, "middle",
            new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero), 2.0, 1.0, "middle");
        var newest = new HighlightEvent(
            PlatformType.Huya, "newest",
            new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.FromHours(8)), 3.0, 1.5, "newest");

        // 故意乱序写入。
        await repository.AddAsync(oldest);
        await repository.AddAsync(newest);
        await repository.AddAsync(middle);

        var recent = await repository.GetRecentAsync();
        Assert.Equal(
            new[] { newest.Id, middle.Id, oldest.Id },
            recent.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task GetRecent_RespectsLimit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(new HighlightEvent(
                PlatformType.Bilibili,
                $"room-{i}",
                BaseTime.AddMinutes(i),
                2.0 + i,
                1.0 + i,
                $"reason-{i}"));
        }

        var topTwo = await repository.GetRecentAsync(2);
        Assert.Equal(2, topTwo.Count);
        Assert.Equal(
            new[] { BaseTime.AddMinutes(4), BaseTime.AddMinutes(3) },
            topTwo.Select(item => item.TriggerTime).ToArray());

        Assert.Equal(5, (await repository.GetRecentAsync(limit: 100)).Count);
        Assert.Single(await repository.GetRecentAsync(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task GetRecent_WithNonPositiveLimit_ReturnsEmpty(int limit)
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        await repository.AddAsync(new HighlightEvent(
            PlatformType.Bilibili, "room-1", BaseTime, 5.0, 1.0, "reason"));

        Assert.Empty(await repository.GetRecentAsync(limit));
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEvents_AndIsIdempotent()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        await repository.AddAsync(new HighlightEvent(
            PlatformType.Bilibili, "room-1", BaseTime, 1.0, 0.5, "first"));
        await repository.AddAsync(new HighlightEvent(
            PlatformType.Douyu, "room-2", BaseTime.AddMinutes(1), 2.0, 1.0, "second"));

        await repository.ClearAsync();
        Assert.Empty(await repository.GetRecentAsync());

        // 空表重复清理不报错。
        await repository.ClearAsync();
        Assert.Empty(await repository.GetRecentAsync());
    }

    [Fact]
    public async Task GetRecent_OnEmptyTable_ReturnsEmpty()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        Assert.Empty(await repository.GetRecentAsync());
        Assert.Empty(await repository.GetRecentAsync(10));
    }

    [Fact]
    public async Task AddAsync_WithSameId_ReplacesExistingRow()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        var id = Guid.NewGuid();
        var original = new HighlightEvent(
            PlatformType.Bilibili, "room-1", BaseTime, 10.5, 2.5, "first")
        {
            Id = id
        };

        await repository.AddAsync(original);
        await repository.AddAsync(original with
        {
            TriggerReason = "updated",
            CurrentRatePerSec = 99.5
        });

        var reloaded = Assert.Single(await repository.GetRecentAsync());
        Assert.Equal(id, reloaded.Id);
        Assert.Equal("updated", reloaded.TriggerReason);
        Assert.Equal(99.5, reloaded.CurrentRatePerSec);
    }

    [Fact]
    public async Task GetRecent_ThrowsInvalidDataException_ForUnknownPlatformValue()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        await repository.AddAsync(new HighlightEvent(
            (PlatformType)999, "room-x", BaseTime, 1.0, 0.5, "bad platform"));

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetRecentAsync());
    }

    [Fact]
    public async Task Events_PersistAcrossDatabaseReinitialization()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new HighlightEventRepository(test.Database);

        var highlightEvent = new HighlightEvent(
            PlatformType.Twitch, "room-persist", BaseTime, 5.5, 1.5, "persist");
        await repository.AddAsync(highlightEvent);

        // 模拟应用重启：同一路径的新实例重新执行幂等建表后仍能读回事件。
        var reopened = new LiveMonitorDatabase(test.Database.DatabasePath);
        await reopened.InitializeAsync();

        var reloaded = Assert.Single(
            await new HighlightEventRepository(reopened).GetRecentAsync());
        Assert.Equal(highlightEvent.Id, reloaded.Id);
        Assert.Equal(highlightEvent.RoomId, reloaded.RoomId);
        Assert.Equal(highlightEvent.TriggerTime, reloaded.TriggerTime);
        Assert.Equal(highlightEvent.TriggerReason, reloaded.TriggerReason);
    }
}
