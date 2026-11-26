using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

/// <summary>主库校验消息与操作状态文案（`RoomCatalogStatusText`）的直接单测。</summary>
public sealed class RoomCatalogStatusTextTests
{
    [Theory]
    [InlineData(RoomCatalogStatusText.MissingUrl, "请粘贴直播间链接或房间号。")]
    [InlineData(RoomCatalogStatusText.InvalidCustomStreamUrl, "自定义清流需要 http(s) / rtmp(s) / rtsp(s) / srt 或 mms 绝对地址。")]
    [InlineData(RoomCatalogStatusText.CustomProviderMissing, "自定义清流 Provider 未注册，无法添加。")]
    [InlineData(RoomCatalogStatusText.DuplicateRoom, "这个直播间已经在主库中，不能重复添加。")]
    [InlineData(RoomCatalogStatusText.AliasRequired, "显示名称不能为空。")]
    [InlineData(RoomCatalogStatusText.DisplayNumberOutOfRange, "序号必须大于 0。")]
    [InlineData(RoomCatalogStatusText.RoomNotFound, "直播间不存在。")]
    [InlineData(RoomCatalogStatusText.InvalidStreamUrl, "清流地址必须是 http(s) / rtmp(s) / rtsp(s) / srt 或 mms 绝对地址。")]
    public void Constant_KeepsExpectedWording(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData("晚风")]
    [InlineData("某位很长的测试主播名")]
    public void Added_EmbedsAlias(string alias)
        => Assert.Equal($"已添加 {alias}", RoomCatalogStatusText.Added(alias));

    [Theory]
    [InlineData("晚风")]
    [InlineData("某位很长的测试主播名")]
    public void Removed_EmbedsAlias(string alias)
        => Assert.Equal($"已从主库删除 {alias}", RoomCatalogStatusText.Removed(alias));

    [Fact]
    public void ValidationMessages_AreNonEmptyAndDistinct()
    {
        var messages = new[]
        {
            RoomCatalogStatusText.MissingUrl,
            RoomCatalogStatusText.InvalidCustomStreamUrl,
            RoomCatalogStatusText.CustomProviderMissing,
            RoomCatalogStatusText.DuplicateRoom,
            RoomCatalogStatusText.AliasRequired,
            RoomCatalogStatusText.DisplayNumberOutOfRange,
            RoomCatalogStatusText.RoomNotFound,
            RoomCatalogStatusText.InvalidStreamUrl,
        };

        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void StatusCopy_IsDistinctFromValidationCopy()
    {
        Assert.NotEqual(RoomCatalogStatusText.Added("A"), RoomCatalogStatusText.Removed("A"));
        Assert.False(RoomCatalogStatusText.Added("X").Contains("不能", StringComparison.Ordinal));
    }
}
