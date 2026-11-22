using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class LayoutDiffServiceTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid D = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void Plan_SeparatesRemovedRetainedAndAdded()
    {
        var plan = LayoutDiffService.Plan(new[] { A, B, C }, new[] { B, C, D });

        Assert.Equal(new[] { A }, plan.Removed);
        Assert.Equal(new[] { B, C }, plan.Retained);
        Assert.Equal(new[] { D }, plan.Added);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void Plan_IgnoresEmptyAndDuplicateRooms()
    {
        var plan = LayoutDiffService.Plan(
            new[] { Guid.Empty, A, A, B },
            new[] { A, A, Guid.Empty, B });

        Assert.Empty(plan.Removed);
        Assert.Equal(new[] { A, B }, plan.Retained);
        Assert.Empty(plan.Added);
        Assert.False(plan.HasChanges);
    }
}
