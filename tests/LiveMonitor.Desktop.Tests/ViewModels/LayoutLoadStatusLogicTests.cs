using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class LayoutLoadStatusLogicTests
{
    [Theory]
    [InlineData(true, "已载入方案「日常」：自由布局，播放器原地保留。")]
    [InlineData(false, "已载入方案「日常」：窗格结构一致，播放器原地保留。")]
    public void InPlace_ChoosesFreeformOrStructureHint(bool isFreeform, string expected)
    {
        Assert.Equal(expected, LayoutLoadStatusLogic.InPlace("日常", isFreeform));
    }

    [Theory]
    [InlineData(0, "已载入方案「Apex」：布局结构变化，已重建 0 路")]
    [InlineData(3, "已载入方案「Apex」：布局结构变化，已重建 3 路")]
    public void Rebuilt_IncludesTileCount(int newTileCount, string expected)
    {
        Assert.Equal(expected, LayoutLoadStatusLogic.Rebuilt("Apex", newTileCount));
    }

    [Theory]
    [InlineData(0, 0, 0, "已载入方案「双人」：复用 0 路，销毁 0 路，新增 0 路")]
    [InlineData(2, 1, 3, "已载入方案「双人」：复用 2 路，销毁 1 路，新增 3 路")]
    public void DiffChanged_IncludesAllCounts(int transferred, int removed, int added, string expected)
    {
        Assert.Equal(expected, LayoutLoadStatusLogic.DiffChanged("双人", transferred, removed, added));
    }

    [Fact]
    public void Plain_OnlyIncludesProfileName()
    {
        Assert.Equal("已载入方案「默认方案」", LayoutLoadStatusLogic.Plain("默认方案"));
    }

    [Theory]
    [InlineData(true, true, "已载入方案「P」：布局结构变化，已重建 4 路")]
    [InlineData(false, true, "已载入方案「P」：复用 2 路，销毁 1 路，新增 3 路")]
    [InlineData(false, false, "已载入方案「P」")]
    public void Describe_PrefersRebuiltThenDiffThenPlain(bool rebuilt, bool hasChanges, string expected)
    {
        var text = LayoutLoadStatusLogic.Describe(
            "P",
            rebuilt,
            hasChanges,
            newTileCount: 4,
            transferred: 2,
            removed: 1,
            added: 3);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Describe_KeepsProfileNameVerbatim()
    {
        var text = LayoutLoadStatusLogic.Describe(
            "方案 1 (1080p)",
            rebuilt: false,
            hasChanges: false,
            newTileCount: 0,
            transferred: 0,
            removed: 0,
            added: 0);

        Assert.Equal("已载入方案「方案 1 (1080p)」", text);
    }
}
