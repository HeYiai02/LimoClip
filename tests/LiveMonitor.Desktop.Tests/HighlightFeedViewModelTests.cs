using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class HighlightFeedViewModelTests
{
    [Fact]
    public void InitialState_IsEmptyAndCollapsed()
    {
        var viewModel = new HighlightFeedViewModel();

        Assert.Empty(viewModel.Items);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.IsVisible);
        Assert.Equal(200, HighlightFeedViewModel.MaxItems);
    }

    [Fact]
    public void UpdateIsVisible_NotifiesOnlyWhenChanged()
    {
        var viewModel = new HighlightFeedViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(viewModel.UpdateIsVisible(true));
        Assert.False(viewModel.UpdateIsVisible(true));

        Assert.True(viewModel.IsVisible);
        Assert.Equal(new[] { nameof(HighlightFeedViewModel.IsVisible) }, changes);
    }

    [Fact]
    public void Insert_AddsNewestFirstAndRequestsScroll()
    {
        var viewModel = new HighlightFeedViewModel();
        var emptyChanges = new List<string?>();
        viewModel.PropertyChanged += (_, e) => emptyChanges.Add(e.PropertyName);
        var scrollRequests = 0;
        viewModel.ScrollToTopRequested += (_, _) => scrollRequests++;

        var first = CreateItem(1);
        var second = CreateItem(2);
        viewModel.Insert(first);
        viewModel.Insert(second);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Same(second, viewModel.Items[0]);
        Assert.Same(first, viewModel.Items[1]);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal(2, scrollRequests);
        Assert.Equal(
            new[] { nameof(HighlightFeedViewModel.IsEmpty), nameof(HighlightFeedViewModel.IsEmpty) },
            emptyChanges);
    }

    [Fact]
    public void Insert_TrimsOldestBeyondMaxItems()
    {
        var viewModel = new HighlightFeedViewModel();

        for (var index = 0; index < HighlightFeedViewModel.MaxItems + 5; index++)
        {
            viewModel.Insert(CreateItem(index));
        }

        Assert.Equal(HighlightFeedViewModel.MaxItems, viewModel.Items.Count);
        Assert.Equal("房间 204", viewModel.Items[0].RoomTitle);
        Assert.Equal("房间 5", viewModel.Items[^1].RoomTitle);
    }

    [Fact]
    public void Insert_WhenAlreadyPopulated_StillNotifiesEmptyState()
    {
        var viewModel = new HighlightFeedViewModel();
        viewModel.Insert(CreateItem(1));
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.Insert(CreateItem(2));

        Assert.Equal(new[] { nameof(HighlightFeedViewModel.IsEmpty) }, changes);
    }

    [Fact]
    public void ReplaceAll_ReplacesInOrderWithoutScroll()
    {
        var viewModel = new HighlightFeedViewModel();
        viewModel.Insert(CreateItem(0));
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var scrollRequests = 0;
        viewModel.ScrollToTopRequested += (_, _) => scrollRequests++;

        viewModel.ReplaceAll(new[] { CreateItem(9), CreateItem(8), CreateItem(7) });

        Assert.Equal(3, viewModel.Items.Count);
        Assert.Equal("房间 9", viewModel.Items[0].RoomTitle);
        Assert.Equal("房间 8", viewModel.Items[1].RoomTitle);
        Assert.Equal("房间 7", viewModel.Items[2].RoomTitle);
        Assert.Equal(0, scrollRequests);
        Assert.Equal(new[] { nameof(HighlightFeedViewModel.IsEmpty) }, changes);
    }

    [Fact]
    public void Clear_EmptiesWithoutScroll()
    {
        var viewModel = new HighlightFeedViewModel();
        viewModel.Insert(CreateItem(1));
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var scrollRequests = 0;
        viewModel.ScrollToTopRequested += (_, _) => scrollRequests++;

        viewModel.Clear();

        Assert.Empty(viewModel.Items);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal(0, scrollRequests);
        Assert.Equal(new[] { nameof(HighlightFeedViewModel.IsEmpty) }, changes);
    }

    [Fact]
    public void ReplaceAll_WithEmptyList_ClearsExistingItems()
    {
        var viewModel = new HighlightFeedViewModel();
        viewModel.Insert(CreateItem(1));

        viewModel.ReplaceAll(Array.Empty<HighlightEventItem>());

        Assert.Empty(viewModel.Items);
        Assert.True(viewModel.IsEmpty);
    }

    private static HighlightEventItem CreateItem(int index)
    {
        var highlightEvent = new HighlightEvent(
            PlatformType.CustomStream,
            $"room-{index}",
            DateTimeOffset.UtcNow.AddSeconds(-index),
            index,
            0,
            $"reason-{index}");
        return new HighlightEventItem(highlightEvent, $"房间 {index}");
    }
}
