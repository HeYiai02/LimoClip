using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class RoomPlacementTests
{
    [Fact]
    public async Task AddRoomToWorkspace_AssignsRoomWithoutStartingPlayback()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var room = new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = 1,
                Platform = PlatformType.Bilibili,
                RoomId = "7734200",
                RawUrl = "https://live.bilibili.com/7734200",
                Alias = "测试房间",
                CreatedAt = DateTime.UtcNow
            };
            await services.Rooms.AddAsync(room);

            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();
            await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);

            Assert.Contains(
                WorkspaceDockBuilder.EnumerateTiles(viewModel.Layout!),
                tile => tile.LiveRoomId == room.Id);
            Assert.Equal("已添加 测试房间", viewModel.StatusText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AddingThreeRooms_AutoFitsToTwoByTwo()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var rooms = Enumerable.Range(1, 3).Select(index => new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = index,
                Platform = PlatformType.Bilibili,
                RoomId = (7734200 + index).ToString(),
                RawUrl = $"https://live.bilibili.com/{7734200 + index}",
                Alias = $"测试房间{index}",
                CreatedAt = DateTime.UtcNow
            }).ToList();
            foreach (var room in rooms)
            {
                await services.Rooms.AddAsync(room);
            }

            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();
            foreach (var room in rooms)
            {
                await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
            }

            Assert.Equal(2, viewModel.GridRows);
            Assert.Equal(2, viewModel.GridColumns);
            var tiles = WorkspaceDockBuilder.EnumerateTiles(viewModel.Layout!).ToList();
            Assert.Equal(3, tiles.Count(tile => tile.HasRoom));
            Assert.All(rooms, room => Assert.Contains(tiles, tile => tile.LiveRoomId == room.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task CustomLayout_FillsEmptyPaneThenAppendsWithoutReordering()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var rooms = Enumerable.Range(1, 5).Select(index => new LiveRoom
            {
                Id = Guid.NewGuid(),
                DisplayNumber = index,
                Platform = PlatformType.Bilibili,
                RoomId = (7734200 + index).ToString(),
                RawUrl = $"https://live.bilibili.com/{7734200 + index}",
                Alias = $"测试房间{index}",
                CreatedAt = DateTime.UtcNow
            }).ToList();
            foreach (var room in rooms)
            {
                await services.Rooms.AddAsync(room);
            }

            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();
            foreach (var room in rooms.Take(3))
            {
                await viewModel.AddRoomToWorkspaceAsync(room.Id, startPlayback: false);
            }

            var outer = (ProportionalDock)viewModel.Layout!.VisibleDockables![0];
            var row = (ProportionalDock)outer.VisibleDockables![0];
            var pane = row.VisibleDockables!.OfType<ToolDock>().First();
            viewModel.BeginCustomLayoutTracking();
            pane.Proportion = 0.7;
            Assert.True(viewModel.IsCustomLayout);

            await viewModel.AddRoomToWorkspaceAsync(rooms[3].Id, startPlayback: false);
            await viewModel.AddRoomToWorkspaceAsync(rooms[4].Id, startPlayback: false);

            var tiles = WorkspaceDockBuilder.EnumerateTiles(viewModel.Layout!).ToList();
            Assert.Equal(5, tiles.Count(tile => tile.HasRoom));
            Assert.True(viewModel.IsCustomLayout);
            Assert.Equal(4, viewModel.GridColumns);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
