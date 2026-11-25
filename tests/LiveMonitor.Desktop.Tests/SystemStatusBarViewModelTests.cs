using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class SystemStatusBarViewModelTests
{
    private const string PaneA = "pane-1";

    private static PaneHealthSample Sample(long droppedFrames, double framesPerSecond)
        => new(PaneA, droppedFrames, framesPerSecond);

    [Fact]
    public void InitialSnapshot_ExposesReadOnlyDefaults()
    {
        using var viewModel = new SystemStatusBarViewModel();

        Assert.Equal("就绪", viewModel.StatusText);
        Assert.Equal("独轮车已拦: 0 人", viewModel.UnicycleBlockedText);
        Assert.Equal(string.Empty, viewModel.RoomCountText);
        Assert.Equal("解码正常 (0% 丢帧)", viewModel.HealthText);
        Assert.False(viewModel.IsHealthWarning);
        Assert.NotNull(viewModel.Metrics);
    }

    [Fact]
    public void UpdateStatusText_NotifiesOnlyWhenChanged()
    {
        using var viewModel = new SystemStatusBarViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(viewModel.UpdateStatusText("已移出 房间 A"));
        Assert.False(viewModel.UpdateStatusText("已移出 房间 A"));

        Assert.Equal("已移出 房间 A", viewModel.StatusText);
        Assert.Equal(new[] { nameof(SystemStatusBarViewModel.StatusText) }, changes);
    }

    [Fact]
    public void UpdateUnicycleBlockedUserCount_FormatsClampsAndDeduplicates()
    {
        using var viewModel = new SystemStatusBarViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.UpdateUnicycleBlockedUserCount(7);
        viewModel.UpdateUnicycleBlockedUserCount(7);
        viewModel.UpdateUnicycleBlockedUserCount(-3);

        Assert.Equal("独轮车已拦: 0 人", viewModel.UnicycleBlockedText);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, name => Assert.Equal(nameof(SystemStatusBarViewModel.UnicycleBlockedText), name));
    }

    [Fact]
    public void UpdateWorkspaceSummary_FormatsGridAndHeroLayout()
    {
        using var viewModel = new SystemStatusBarViewModel();

        viewModel.UpdateWorkspaceSummary(3, isHeroLayout: false, workspaceRoomCount: 2, gridRows: 2, gridColumns: 3);
        Assert.Equal("主库 3 路 · 工作区 2×3", viewModel.RoomCountText);

        viewModel.UpdateWorkspaceSummary(5, isHeroLayout: true, workspaceRoomCount: 2, gridRows: 2, gridColumns: 3);
        Assert.Equal("主库 5 路 · 工作区 主副屏（2 路）", viewModel.RoomCountText);
    }

    [Fact]
    public void UpdateHealth_FirstSampleOnlyEstablishesBaseline()
    {
        using var viewModel = new SystemStatusBarViewModel();

        viewModel.UpdateHealth(new[] { Sample(100, 30) });

        Assert.Equal("解码正常 (0% 丢帧)", viewModel.HealthText);
        Assert.False(viewModel.IsHealthWarning);
    }

    [Fact]
    public void UpdateHealth_AboveThresholdSetsWarningAndNotifiesInOrder()
    {
        using var viewModel = new SystemStatusBarViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.UpdateHealth(new[] { Sample(100, 30) });
        viewModel.UpdateHealth(new[] { Sample(101, 30) });

        Assert.Equal("解码正常 (2% 丢帧)", viewModel.HealthText);
        Assert.False(viewModel.IsHealthWarning);

        changes.Clear();
        viewModel.UpdateHealth(new[] { Sample(106, 30) });

        Assert.True(viewModel.IsHealthWarning);
        Assert.Equal("解码告警 (8.3% 丢帧)", viewModel.HealthText);
        Assert.Equal(
            new[] { nameof(SystemStatusBarViewModel.IsHealthWarning), nameof(SystemStatusBarViewModel.HealthText) },
            changes);
    }

    [Fact]
    public void UpdateHealth_MatchesStandaloneCoordinator()
    {
        using var viewModel = new SystemStatusBarViewModel();
        var coordinator = new HealthSummaryCoordinator();
        var rounds = new[]
        {
            new[] { Sample(0, 30) },
            new[] { Sample(1, 30) },
            new[] { Sample(2, 30) },
            new[] { Sample(20, 30) },
            new[] { Sample(20, 0) }
        };

        foreach (var samples in rounds)
        {
            var expected = coordinator.Update(samples);
            viewModel.UpdateHealth(samples);

            Assert.Equal(expected.IsWarning, viewModel.IsHealthWarning);
            Assert.Equal(expected.Text, viewModel.HealthText);
        }
    }

    [Fact]
    public void UpdateHealth_AtFivePercentThresholdIsWarning()
    {
        using var viewModel = new SystemStatusBarViewModel();
        viewModel.UpdateHealth(new[] { Sample(0, 30) });

        viewModel.UpdateHealth(new[] { Sample(3, 30) });

        Assert.True(viewModel.IsHealthWarning);
        Assert.Equal("解码告警 (5.0% 丢帧)", viewModel.HealthText);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var viewModel = new SystemStatusBarViewModel();

        var exception = Record.Exception(() =>
        {
            viewModel.Dispose();
            viewModel.Dispose();
        });

        Assert.Null(exception);
    }
}
