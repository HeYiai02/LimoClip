using System.Text;
using LiveMonitor.Media.Editing;
using Xunit;

namespace LiveMonitor.Media.Tests;

public sealed class DanmakuJsonlReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LiveMonitor.Media.Tests.Jsonl",
        Guid.NewGuid().ToString("N"));

    public DanmakuJsonlReaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task ReadAllAsync_SkipsMetaBadLinesAndKeepsValidItemsInOrder()
    {
        var path = Path.Combine(_directory, "recording.jsonl");
        var lines = new[]
        {
            """{"type":"meta","version":1,"sessionId":"57702b6d-383b-4ec3-bcd4-01d2b5c9ede1","platform":1,"roomId":"6650029"}""",
            """{"type":"dm","offsetMs":21095,"uid":"1","uname":"\u5C0F\u6768\u5B63\u5F55\u5458","text":"\u8D5B\u7A0B","isSticker":false,"isGift":false}""",
            """{"type":"unknown","offsetMs":22000}""",
            """{"type":"dm","offsetMs":5000,"uname":"a","text":"later line but earlier offset"}""",
            """{"type":"dm","offsetMs":-50,"uname":"b","text":"negative"}""",
            """{"type":"dm","uname":"c","text":"missing offset"}""",
            """{"type":"dm","offsetMs":30000,"uname":"d","text":"sticker","isSticker":true}""",
            """{"type":"dm","offsetMs":40000,"uname":"e","text":"truncated""",
            "",
            "not json at all"
        };

        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));

        var items = await DanmakuJsonlReader.ReadAllAsync(path);

        Assert.Equal(4, items.Count);
        Assert.Equal(new[] { 0L, 5000L, 21095L, 30000L }, items.Select(i => i.OffsetMs));
        Assert.Equal("b", items[0].SenderName); // 负偏移被夹到 0
        Assert.Equal("小杨季录员", items[2].SenderName);
        Assert.Equal("赛程", items[2].Text);
        Assert.True(items[3].IsSticker);
        Assert.False(items[3].IsGift);
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsEmptyForMetaOnlyFile()
    {
        var path = Path.Combine(_directory, "meta-only.jsonl");
        await File.WriteAllTextAsync(path, """{"type":"meta","version":1}""" + Environment.NewLine);

        var items = await DanmakuJsonlReader.ReadAllAsync(path);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAllAsync_ThrowsWhenFileMissing()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => DanmakuJsonlReader.ReadAllAsync(Path.Combine(_directory, "missing.jsonl")));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("""{"type":"meta"}""", false)]
    [InlineData("""{"type":"dm"}""", false)]
    [InlineData("""{"type":"dm","offsetMs":"12"}""", false)]
    [InlineData("""{"type":"dm","offsetMs":12}""", true)]
    [InlineData("""{"offsetMs":34,"uname":"u","text":"t"}""", true)]
    public void TryParseLine_HandlesMalformedInput(string line, bool expected)
    {
        Assert.Equal(expected, DanmakuJsonlReader.TryParseLine(line, out _));
    }
}
