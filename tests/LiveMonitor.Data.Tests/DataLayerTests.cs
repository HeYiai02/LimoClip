using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Data.Repositories;
using Xunit;

namespace LiveMonitor.Data.Tests;

public sealed class DataLayerTests
{
    [Fact]
    public async Task Initialize_CreatesAllTables_AndEnablesWalAndForeignKeys()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var connection = test.Database.OpenConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Equal("wal", journalMode?.ToLowerInvariant());

        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('LiveRooms', 'LayoutProfiles', 'LayoutSlots',
                           'PlatformCredentials', 'DanmakuBlockKeywords', 'AppSettings');
            """;
        Assert.Equal(6L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RoomRepository_AssignsDisplayNumbers_AndRejectsDuplicatePlatformRoom()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new RoomRepository(test.Database);

        var first = NewRoom("1001", "主播A");
        var second = NewRoom("1002", "主播B");

        await repository.AddAsync(first);
        await repository.AddAsync(second);

        Assert.Equal(1, first.DisplayNumber);
        Assert.Equal(2, second.DisplayNumber);
        Assert.True(await repository.ExistsAsync(PlatformType.Bilibili, "1001"));
        Assert.Equal(3, await repository.GetNextDisplayNumberAsync());

        var duplicate = NewRoom("1001", "重复");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(duplicate));

        var all = await repository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { 1, 2 }, all.Select(room => room.DisplayNumber).ToArray());
    }

    [Fact]
    public async Task RoomDelete_CascadesLayoutSlots()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var profiles = new LayoutProfileRepository(test.Database);

        var room = NewRoom("2001", "主播");
        await rooms.AddAsync(room);

        var profile = new LayoutProfile
        {
            Name = "双屏",
            Slots =
            {
                new LayoutSlot { LiveRoomId = room.Id, PaneId = "pane-1" }
            }
        };
        await profiles.SaveAsync(profile);

        await rooms.DeleteAsync(room.Id);

        var reloaded = await profiles.GetByIdAsync(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Slots);
    }

    [Fact]
    public async Task LayoutProfileRepository_RoundTripsAndReplacesSlots()
    {
        await using var test = await TestDatabase.CreateAsync();
        var rooms = new RoomRepository(test.Database);
        var profiles = new LayoutProfileRepository(test.Database);

        var firstRoom = NewRoom("3001", "A");
        var secondRoom = NewRoom("3002", "B");
        await rooms.AddAsync(firstRoom);
        await rooms.AddAsync(secondRoom);

        var profile = new LayoutProfile
        {
            Name = "晚间开黑",
            IsCustomLayout = true,
            DockLayoutJson = "{\"rows\":2,\"cols\":2}",
            Slots =
            {
                new LayoutSlot
                {
                    LiveRoomId = firstRoom.Id,
                    PaneId = "pane-1",
                    DefaultMuted = false,
                    DefaultDanmakuVisible = true,
                    IsFocusSlot = true,
                    PreferredQualityId = "original"
                },
                new LayoutSlot
                {
                    LiveRoomId = secondRoom.Id,
                    PaneId = "pane-2"
                }
            }
        };

        await profiles.SaveAsync(profile);
        Assert.NotEqual(Guid.Empty, profile.Id);

        var reloaded = await profiles.GetByIdAsync(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("晚间开黑", reloaded.Name);
        Assert.Equal("{\"rows\":2,\"cols\":2}", reloaded.DockLayoutJson);
        Assert.True(reloaded.IsCustomLayout);
        Assert.Equal(2, reloaded.Slots.Count);
        var focus = Assert.Single(reloaded.Slots, slot => slot.PaneId == "pane-1");
        Assert.False(focus.DefaultMuted);
        Assert.True(focus.DefaultDanmakuVisible);
        Assert.True(focus.IsFocusSlot);
        Assert.Equal("original", focus.PreferredQualityId);

        profile.Slots = new List<LayoutSlot>
        {
            new() { LiveRoomId = secondRoom.Id, PaneId = "pane-9" }
        };
        await profiles.SaveAsync(profile);

        var replaced = await profiles.GetByIdAsync(profile.Id);
        Assert.NotNull(replaced);
        var only = Assert.Single(replaced.Slots);
        Assert.Equal("pane-9", only.PaneId);
        Assert.Equal(profile.Id, only.LayoutProfileId);

        Assert.Single(await profiles.GetAllAsync());

        await profiles.DeleteAsync(profile.Id);
        Assert.Null(await profiles.GetByIdAsync(profile.Id));
    }

    [Fact]
    public async Task LayoutProfile_RejectsDuplicatePaneIds()
    {
        await using var test = await TestDatabase.CreateAsync();
        var profiles = new LayoutProfileRepository(test.Database);
        var profile = new LayoutProfile
        {
            Name = "重复窗格",
            Slots =
            {
                new LayoutSlot { LiveRoomId = Guid.NewGuid(), PaneId = "pane-1" },
                new LayoutSlot { LiveRoomId = Guid.NewGuid(), PaneId = "pane-1" }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.SaveAsync(profile));
        Assert.Empty(await profiles.GetAllAsync());
    }

    [Fact]
    public async Task LayoutProfile_RejectsDuplicateRoomsInSameProfile()
    {
        await using var test = await TestDatabase.CreateAsync();
        var profiles = new LayoutProfileRepository(test.Database);
        var roomId = Guid.NewGuid();
        var profile = new LayoutProfile
        {
            Name = "重复房间",
            Slots =
            {
                new LayoutSlot { LiveRoomId = roomId, PaneId = "pane-1" },
                new LayoutSlot { LiveRoomId = roomId, PaneId = "pane-2" }
            }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.SaveAsync(profile));
        Assert.Contains("直播间重复", error.Message);
        Assert.Empty(await profiles.GetAllAsync());
    }

    [Fact]
    public async Task BlocklistRepository_Crud_AndRejectsDuplicateKeyword()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new BlocklistRepository(test.Database);

        var keyword = new DanmakuBlockKeyword { Keyword = "独轮车" };
        await repository.AddAsync(keyword);

        Assert.True(await repository.ExistsAsync("独轮车"));
        Assert.Single(await repository.GetAllAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(new DanmakuBlockKeyword { Keyword = "独轮车" }));

        await repository.DeleteAsync(keyword.Id);
        Assert.False(await repository.ExistsAsync("独轮车"));
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task AppSettingsRepository_UpsertsAndDeletes()
    {
        await using var test = await TestDatabase.CreateAsync();
        var repository = new AppSettingsRepository(test.Database);

        Assert.Null(await repository.GetAsync("BlockUnicycleEnabled"));

        await repository.SetAsync("BlockUnicycleEnabled", "true");
        Assert.Equal("true", await repository.GetAsync("BlockUnicycleEnabled"));

        await repository.SetAsync("BlockUnicycleEnabled", "false");
        Assert.Equal("false", await repository.GetAsync("BlockUnicycleEnabled"));

        await repository.SetAsync("DanmakuPreviewEnabled", "true");
        var all = await repository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("false", all["BlockUnicycleEnabled"]);

        await repository.DeleteAsync("BlockUnicycleEnabled");
        Assert.Null(await repository.GetAsync("BlockUnicycleEnabled"));
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task CredentialStore_RoundTripsDpapi_AndMarksExpired()
    {
        await using var test = await TestDatabase.CreateAsync();
        var store = new CredentialStore(test.Database);
        const string cookie = "SESSDATA=secret-value";

        Assert.Null(await store.GetCookieAsync(PlatformType.Bilibili));

        await store.SaveAsync(PlatformType.Bilibili, "测试账号", cookie);
        Assert.Equal(cookie, await store.GetCookieAsync(PlatformType.Bilibili));
        Assert.False(await store.IsExpiredAsync(PlatformType.Bilibili));

        await using (var connection = test.Database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT EncryptedCookie FROM PlatformCredentials WHERE Platform = $platform;";
            command.Parameters.AddWithValue("$platform", (int)PlatformType.Bilibili);
            var stored = Convert.ToString(await command.ExecuteScalarAsync());

            Assert.NotNull(stored);
            Assert.NotEqual(cookie, stored);
            Assert.DoesNotContain("SESSDATA", stored);
            Assert.DoesNotContain("secret-value", stored);
        }

        await store.MarkExpiredAsync(PlatformType.Bilibili);
        Assert.True(await store.IsExpiredAsync(PlatformType.Bilibili));
        Assert.Null(await store.GetCookieAsync(PlatformType.Bilibili));

        await store.SaveAsync(PlatformType.Bilibili, null, cookie);
        Assert.False(await store.IsExpiredAsync(PlatformType.Bilibili));
        Assert.Equal(cookie, await store.GetCookieAsync(PlatformType.Bilibili));

        await store.RemoveAsync(PlatformType.Bilibili);
        Assert.Null(await store.GetCookieAsync(PlatformType.Bilibili));
    }

    private static LiveRoom NewRoom(string roomId, string alias) => new()
    {
        Platform = PlatformType.Bilibili,
        RoomId = roomId,
        RawUrl = $"https://live.bilibili.com/{roomId}",
        Alias = alias
    };
}
