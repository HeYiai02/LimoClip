using LiveMonitor.Core.Entities;
using LiveMonitor.Media.Recording;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class RecordingFileNamingTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 23, 8, 5, 6, TimeSpan.Zero);

    [Fact]
    public void BuildBaseName_UsesTimestampDisplayNumberAliasAndRoomId()
    {
        var name = RecordingFileNaming.BuildBaseName(
            displayNumber: 3,
            alias: "晚风/游戏:室",
            roomId: "21452505",
            startedAtUtc: StartedAt);

        Assert.Equal("20261123_080506_#3_晚风_游戏_室_21452505", name);
    }

    [Fact]
    public void BuildBaseName_FromRoom_MatchesExplicitOverload()
    {
        var room = new LiveRoom
        {
            DisplayNumber = 7,
            Alias = "晚风游戏室",
            RoomId = "21452505",
        };

        var fromRoom = RecordingFileNaming.BuildBaseName(room, StartedAt);
        var explicitName = RecordingFileNaming.BuildBaseName(7, "晚风游戏室", "21452505", StartedAt);

        Assert.Equal(explicitName, fromRoom);
    }

    [Fact]
    public void BuildBaseName_UsesFallbacksWhenAliasAndRoomAreMissing()
    {
        var name = RecordingFileNaming.BuildBaseName(5, alias: null, roomId: "  ", StartedAt);

        Assert.Equal("20261123_080506_#5_直播5_room", name);
    }

    [Fact]
    public void Sanitize_ReturnsFallbackForBlankOrEmptyAfterCleaning()
    {
        Assert.Equal("未命名", RecordingFileNaming.Sanitize(null));
        Assert.Equal("未命名", RecordingFileNaming.Sanitize("   "));
        Assert.Equal("自定义", RecordingFileNaming.Sanitize(" . ", fallback: "自定义"));
    }

    [Fact]
    public void Sanitize_ReplacesInvalidAndControlCharacters()
    {
        Assert.Equal("晚风_游戏_室", RecordingFileNaming.Sanitize("晚风/游戏:室"));
        Assert.Equal("a_b", RecordingFileNaming.Sanitize("a\u0001b"));
    }

    [Fact]
    public void Sanitize_TrimsTrailingDotsAndSpaces()
    {
        Assert.Equal("abc", RecordingFileNaming.Sanitize("abc. "));
    }

    [Fact]
    public void Sanitize_TruncatesToMaxSegmentLength()
    {
        var longName = new string('a', 100);

        var sanitized = RecordingFileNaming.Sanitize(longName);

        Assert.Equal(RecordingFileNaming.MaxSegmentLength, sanitized.Length);
        Assert.Equal(80, RecordingFileNaming.MaxSegmentLength);
    }
}
